// Tank Arena bots: a load and end-to-end test for the server.
//   dotnet run --project tests/GameServer.ArenaBots -- <baseUrl> <adminKey> [bots=8] [--keep]
// Writes the arena config (like the web page does), creates N players, queues them in the matchmaker, plays full
// server-side matches over websocket, then checks that coins, stats, quests and leaderboards were updated by the server.
// Log: arena-bots.log. Exit code 0 = everything passed.
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var baseUrl = (args.Length > 0 ? args[0] : "http://localhost:5000").TrimEnd('/');
var adminKey = args.Length > 1 ? args[1] : "dev-admin-key-do-not-use-in-production-0123456789";
var botCount = args.Length > 2 && int.TryParse(args[2], out var n) ? Math.Clamp(n, 2, 200) : 8;
if (botCount % 2 == 1) botCount++;
var keep = args.Contains("--keep");
var run = Guid.NewGuid().ToString("N")[..6];
var logPath = "arena-bots.log";
File.WriteAllText(logPath, "");
var logLock = new object();
int passed = 0, failed = 0;

void Log(string s)
{
    var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
    lock (logLock) { Console.WriteLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
}
void Check(string name, bool ok, string detail = "")
{
    if (ok) { Interlocked.Increment(ref passed); Log("PASS  " + name); }
    else { Interlocked.Increment(ref failed); Log("FAIL  " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

Log($"Arena bots, run {run}, server {baseUrl}, {botCount} bots");

// ---- 1. config ------------------------------------------------------------------------------------------------
string cfgText;
try { cfgText = await http.GetStringAsync("/arena/arena-config.json"); }
catch (Exception ex) { Log("FAIL  cannot read /arena/arena-config.json: " + ex.Message); return 1; }

JsonNode Fix(JsonNode? node)
{
    switch (node)
    {
        case JsonObject o:
            var copy = new JsonObject();
            foreach (var kv in o) copy[kv.Key] = Fix(kv.Value);
            return copy;
        case JsonArray a:
            var arr = new JsonArray();
            foreach (var x in a) arr.Add(Fix(x));
            return arr;
        case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith("@now"):
            var days = int.Parse(new string(s.Where(char.IsDigit).ToArray()));
            return JsonValue.Create(DateTime.UtcNow.AddDays(s.Contains('+') ? days : -days).ToString("o"))!;
        default:
            return node?.DeepClone() ?? JsonValue.Create((string?)null)!;
    }
}

var configs = JsonNode.Parse(cfgText)!["configs"]!.AsObject();
var allWritten = true;
foreach (var kv in configs)
{
    using var req = new HttpRequestMessage(HttpMethod.Put, "/admin/api/config/" + kv.Key)
    {
        Content = new StringContent(new JsonObject { ["value"] = Fix(kv.Value), ["public"] = false }.ToJsonString(), Encoding.UTF8, "application/json")
    };
    req.Headers.Add("X-Admin-Key", adminKey);
    using var res = await http.SendAsync(req);
    if (!res.IsSuccessStatusCode) { allWritten = false; Log($"      config {kv.Key}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}"); }
}
Check("game config written (matches, shop, quests, achievements, leaderboards, tournament)", allWritten);
Log("      waiting for the mode cache (3.5 s)");
await Task.Delay(3500);

// ---- 2. bots ----------------------------------------------------------------------------------------------------
var bots = new List<Bot>();
for (var i = 0; i < botCount; i++)
    bots.Add(new Bot(i, $"bot-{run}-{i:D3}-padding-padding", $"bot{run}{i:D3}", baseUrl, http, json, Log));

var logins = await Task.WhenAll(bots.Select(b => b.LoginAsync()));
Check($"{botCount} bots logged in", logins.All(x => x));
if (logins.Any(x => !x)) return Finish(1);

var coinsBefore = new Dictionary<int, long>();
foreach (var b in bots) coinsBefore[b.Index] = await b.CoinsAsync();

var started = DateTime.UtcNow;
Log($"      queueing {botCount} bots for 'arena' and playing…");
var results = await Task.WhenAll(bots.Select(b => b.PlayOneMatchAsync(TimeSpan.FromSeconds(150))));
Log($"      all matches finished in {(DateTime.UtcNow - started).TotalSeconds:F1}s");

Check("every bot was matched", results.All(r => r.Matched), string.Join(",", results.Where(r => !r.Matched).Select(r => r.Index)));
Check("every bot connected to its match and got 'init'", results.All(r => r.GotInit));
Check("every bot received the server's result ('over')", results.All(r => r.Over), string.Join(",", results.Where(r => !r.Over).Select(r => r.Index)));
Check("the server streamed snapshots (>= 15 per second on average)", results.Where(r => r.Over).All(r => r.SnapshotsPerSecond >= 15),
    string.Join(",", results.Select(r => r.SnapshotsPerSecond.ToString("F1"))));
Check("no huge gap between snapshots (< 1000 ms)", results.Where(r => r.Over).All(r => r.MaxGapMs < 1000), string.Join(",", results.Select(r => r.MaxGapMs)));
var wins = results.Count(r => r.Won);
var draws = results.Count(r => r.Draw);
Check("each match has exactly one winner (or a draw)", wins * 2 + draws == results.Count(r => r.Over), $"wins={wins} draws={draws}");
Log($"      avg input->state latency: {results.Where(r => r.Over).Select(r => r.AvgRttMs).DefaultIfEmpty(0).Average():F0} ms");

// ---- 3. what the server recorded --------------------------------------------------------------------------------
await Task.Delay(1000);
var statsOk = true; var coinsOk = true; var questOk = true;
foreach (var b in bots)
{
    var stats = await b.GetAsync("/v1/stats");
    var played = stats?["stats"]?["matches_played"]?.GetValue<long>() ?? 0;
    if (played < 1) statsOk = false;
    var coins = await b.CoinsAsync();
    var r = results[b.Index];
    if (r.Over && r.Coins > 0 && coins < coinsBefore[b.Index] + r.Coins) coinsOk = false;
    var quests = await b.GetAsync("/v1/quests");
    var q = quests?["quests"]?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<string>() == "play_3");
    if ((q?["progress"]?.GetValue<long>() ?? 0) < 1) questOk = false;
}
Check("server recorded stat matches_played for every bot", statsOk);
Check("server paid the match coins", coinsOk);
Check("quest progress 'play_3' moved", questOk);

var board = await bots[0].GetAsync("/v1/leaderboards/arena_wins?limit=50");
var boardCount = board?["entries"]?.AsArray().Count ?? 0;
Check("leaderboard arena_wins has the winners", boardCount >= wins && wins > 0 || wins == 0, $"entries={boardCount} wins={wins}");

var ach = await bots.First(b => results[b.Index].Over).GetAsync("/v1/achievements");
var first = ach?["achievements"]?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<string>() == "first_match");
Check("achievement 'first_match' is completed and claimable", first?["completed"]?.GetValue<bool>() == true);
if (first?["completed"]?.GetValue<bool>() == true)
{
    var claim = await bots.First(b => results[b.Index].Over).PostAsync("/v1/achievements/first_match/claim");
    Check("claiming it pays the reward", claim.ok, claim.body);
}

// shop: buy a skin with the match coins (winner has >= 60 coins + welcome gift)
var buyer = bots.FirstOrDefault(b => results[b.Index].Won);
if (buyer is not null)
{
    var buy = await buyer.PostAsync("/v1/shop/skin_red/buy", new { quantity = 1, requestId = Guid.NewGuid().ToString() });
    Check("winner buys a skin in the shop", buy.ok, buy.body);
}

// ---- 4. cleanup -------------------------------------------------------------------------------------------------
if (!keep)
{
    foreach (var b in bots) await b.DeleteAccountAsync();
    Log("      accounts deleted");
}
return Finish(failed > 0 ? 1 : 0);

int Finish(int code)
{
    Log("");
    Log($"RESULT: {passed} passed, {failed} failed");
    Log(code == 0 && failed == 0 ? "ALL GOOD" : "SOME CHECKS FAILED");
    return code;
}

// =================================================================================================================
sealed record BotResult(int Index)
{
    public bool Matched, GotInit, Over, Won, Draw;
    public int Coins;
    public double SnapshotsPerSecond;
    public long MaxGapMs;
    public double AvgRttMs;
}

sealed class Bot
{
    private readonly string _deviceId, _username, _baseUrl;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly Action<string> _log;
    private string _token = "", _userId = "";

    public Bot(int index, string deviceId, string username, string baseUrl, HttpClient http, JsonSerializerOptions json, Action<string> log)
    {
        Index = index; _deviceId = deviceId; _username = username; _baseUrl = baseUrl; _http = http; _json = json; _log = log;
    }

    public int Index { get; }

    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var r = new HttpRequestMessage(m, path);
        if (_token.Length > 0) r.Headers.Add("Authorization", "Bearer " + _token);
        if (body is not null) r.Content = JsonContent.Create(body, options: _json);
        return r;
    }

    public async Task<bool> LoginAsync()
    {
        try
        {
            using var res = await _http.SendAsync(Req(HttpMethod.Post, "/v1/auth/device", new { deviceId = _deviceId, username = _username, create = true }));
            var node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            if (!res.IsSuccessStatusCode) { _log($"      bot {Index} login failed: {node}"); return false; }
            _token = node!["token"]!.GetValue<string>();
            _userId = node["userId"]!.GetValue<string>();
            return true;
        }
        catch (Exception ex) { _log($"      bot {Index} login error: {ex.Message}"); return false; }
    }

    public async Task<JsonNode?> GetAsync(string path)
    {
        using var res = await _http.SendAsync(Req(HttpMethod.Get, path));
        var text = await res.Content.ReadAsStringAsync();
        return res.IsSuccessStatusCode && text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    public async Task<(bool ok, string body)> PostAsync(string path, object? body = null)
    {
        using var res = await _http.SendAsync(Req(HttpMethod.Post, path, body));
        return (res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    public async Task<long> CoinsAsync() => (await GetAsync("/v1/wallet"))?["wallet"]?["coins"]?.GetValue<long>() ?? 0;

    public async Task DeleteAccountAsync()
    {
        try { using var res = await _http.SendAsync(Req(HttpMethod.Delete, "/v1/account")); }
        catch { /* best effort */ }
    }

    public async Task<BotResult> PlayOneMatchAsync(TimeSpan timeout)
    {
        var result = new BotResult(Index);
        using var cts = new CancellationTokenSource(timeout);
        var ct = cts.Token;
        try
        {
            // ---- realtime socket: sign in and queue
            using var rt = new ClientWebSocket();
            await rt.ConnectAsync(WsUri("/ws"), ct);
            await SendJson(rt, new { type = "auth", id = "a", data = new { token = _token } }, ct);
            var authOk = await ReadUntil(rt, f => (string?)f?["type"] == "auth.ok" || (string?)f?["type"] == "error", ct);
            if ((string?)authOk?["type"] != "auth.ok") { _log($"      bot {Index} realtime auth failed: {authOk}"); return result; }

            await SendJson(rt, new { type = "matchmaker.add", id = "m", data = new { mode = "arena", properties = new { } } }, ct);
            var matched = await ReadUntil(rt, f => (string?)f?["type"] == "matchmaker.matched" || ((string?)f?["type"] == "error" && (string?)f?["id"] == "m"), ct);
            if ((string?)matched?["type"] != "matchmaker.matched") { _log($"      bot {Index} matchmaker: {matched}"); return result; }
            result.Matched = true;
            var data = matched["data"]!;
            var token = data["token"]!.GetValue<string>();
            var path = data["endpoint"]?["path"]?.GetValue<string>() ?? "/match";

            // ---- match socket
            using var ms = new ClientWebSocket();
            await ms.ConnectAsync(WsUri($"{path}?token={Uri.EscapeDataString(token)}"), ct);
            await SendBinary(ms, new { t = "hello", skin = "" }, ct);

            var you = -1;
            double ex = 400, ey = 250, mx = 80, my = 250;
            var seq = 0;
            var sent = new Dictionary<int, long>();
            var rtts = new List<double>();
            var snaps = 0;
            long lastSnap = 0, maxGap = 0;
            var firstSnap = 0L;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random(Index * 7919 + 13);
            var dir = rnd.Next(4);
            // A ClientWebSocket must not be read with a short cancellable timeout (cancelling aborts it), so one task reads
            // all frames into a channel and this loop handles them every ~50 ms.
            var inbox = System.Threading.Channels.Channel.CreateUnbounded<string>();
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                try
                {
                    while (ms.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var sb = new StringBuilder();
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await ms.ReceiveAsync(buffer, ct);
                            if (r.MessageType == WebSocketMessageType.Close) return;
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                        } while (!r.EndOfMessage);
                        inbox.Writer.TryWrite(sb.ToString());
                    }
                }
                catch (Exception) { /* closed or timed out */ }
                finally { inbox.Writer.TryComplete(); }
            }, ct);

            while (!result.Over && !ct.IsCancellationRequested)
            {
                // send input every ~50 ms
                if (ms.State == WebSocketState.Open)
                {
                    if (rnd.Next(20) == 0) dir = rnd.Next(4);
                    var dx = dir == 0 ? 1 : dir == 1 ? -1 : 0;
                    var dy = dir == 2 ? 1 : dir == 3 ? -1 : 0;
                    var aim = Math.Atan2(ey - my, ex - mx);
                    seq++;
                    sent[seq] = sw.ElapsedMilliseconds;
                    await SendBinary(ms, new { t = "in", dx, dy, aim = Math.Round(aim, 2), fire = true, seq }, ct);
                }

                try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }

                var gotAny = false;
                while (inbox.Reader.TryRead(out var text))
                {
                    gotAny = true;
                    JsonNode? m;
                    try { m = JsonNode.Parse(text); } catch { continue; }
                    switch ((string?)m?["t"])
                    {
                        case "init":
                            result.GotInit = true;
                            you = m!["you"]!.GetValue<int>();
                            break;
                        case "s":
                            {
                                var now = sw.ElapsedMilliseconds;
                                if (firstSnap == 0) firstSnap = now;
                                if (lastSnap != 0) maxGap = Math.Max(maxGap, now - lastSnap);
                                lastSnap = now; snaps++;
                                foreach (var p in m!["p"]!.AsArray())
                                {
                                    var slot = p![0]!.GetValue<int>();
                                    if (slot == you)
                                    {
                                        mx = p[1]!.GetValue<double>(); my = p[2]!.GetValue<double>();
                                        var echoed = p[5]!.GetValue<long>();
                                        if (sent.TryGetValue((int)echoed, out var at)) { rtts.Add(now - at); sent.Remove((int)echoed); }
                                    }
                                    else { ex = p[1]!.GetValue<double>(); ey = p[2]!.GetValue<double>(); }
                                }
                                break;
                            }
                        case "over":
                            {
                                result.Over = true;
                                var winner = m!["winner"]!.GetValue<int>();
                                result.Won = winner == you;
                                result.Draw = winner == -1;
                                result.Coins = m["results"]!.AsArray().FirstOrDefault(x => x!["slot"]!.GetValue<int>() == you)?["coins"]?.GetValue<int>() ?? 0;
                                break;
                            }
                        case "warn":
                            _log($"      bot {Index}: server warning: {m!["msg"]}");
                            break;
                    }
                }

                if (!gotAny && ms.State != WebSocketState.Open) break; // closed by the server
            }

            var seconds = Math.Max(1, (lastSnap - firstSnap) / 1000.0);
            result.SnapshotsPerSecond = snaps / seconds;
            result.MaxGapMs = maxGap;
            result.AvgRttMs = rtts.Count > 0 ? rtts.Average() : 0;
            try { await ms.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            try { await rt.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
        catch (Exception ex)
        {
            _log($"      bot {Index} error: {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    private Uri WsUri(string pathAndQuery) =>
        new((_baseUrl.StartsWith("https") ? "wss" : "ws") + "://" + new Uri(_baseUrl).Authority + pathAndQuery);

    private static Task SendJson(ClientWebSocket ws, object o, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), WebSocketMessageType.Text, true, ct);

    private static Task SendBinary(ClientWebSocket ws, object o, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), WebSocketMessageType.Binary, true, ct);

    private static async Task<JsonNode?> ReadUntil(ClientWebSocket ws, Func<JsonNode?, bool> done, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);

            JsonNode? node;
            try { node = JsonNode.Parse(sb.ToString()); } catch { continue; }
            if (done(node)) return node;
        }
        return null;
    }
}

// End-to-end smoke test for a RUNNING GameServer. Usage:
//   dotnet run --project tests/GameServer.SmokeTest -- [baseUrl] [adminKey]
// Writes everything it does to the console and to smoke-test.log. Exit code 0 = every check passed.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var baseUrl = (args.Length > 0 ? args[0] : "http://localhost:5000").TrimEnd('/');
var adminKey = args.Length > 1 ? args[1] : "dev-admin-key-do-not-use-in-production-0123456789";
var logPath = Path.Combine(Directory.GetCurrentDirectory(), "smoke-test.log");
File.WriteAllText(logPath, "");

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var passed = 0;
var failed = 0;
var run = Guid.NewGuid().ToString("N")[..8];

void Log(string line)
{
    var text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
    Console.WriteLine(text);
    File.AppendAllText(logPath, text + Environment.NewLine);
}

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Log($"PASS  {name}"); }
    else { failed++; Log($"FAIL  {name}{(detail is null ? "" : "  -> " + detail)}"); }
}

void Info(string text) => Log("INFO  " + text);

async Task Section(string title, Func<Task> body)
{
    Log("");
    Log($"=== {title} ===");
    try { await body(); }
    catch (Exception ex)
    {
        failed++;
        Log($"FAIL  section '{title}' crashed: {ex.GetType().Name}: {ex.Message}");
        Log(ex.StackTrace ?? "");
    }
}

async Task<Resp> Call(string method, string path, object? body = null, string? token = null, bool admin = false)
{
    var req = new HttpRequestMessage(new HttpMethod(method), baseUrl + path);
    if (token is not null) req.Headers.Add("Authorization", "Bearer " + token);
    if (admin) req.Headers.Add("X-Admin-Key", adminKey);
    if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    var sw = Stopwatch.StartNew();
    using var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    JsonElement? json = null;
    try { if (text.Length > 0) json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }

    var shown = text.Length > 300 ? text[..300] + "..." : text;
    Log($"HTTP  {method} {path} -> {(int)res.StatusCode} ({sw.ElapsedMilliseconds} ms) {(res.IsSuccessStatusCode ? "" : shown)}");
    return new Resp((int)res.StatusCode, text, json);
}

static string? S(JsonElement? el, params string[] path)
{
    if (el is null) return null;
    var cur = el.Value;
    foreach (var p in path)
    {
        if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
    }
    return cur.ValueKind switch
    {
        JsonValueKind.String => cur.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => cur.GetRawText()
    };
}

static string? Err(Resp r) => S(r.Json, "error");

async Task<Player> NewPlayer(string label)
{
    var deviceId = ("smoke" + label + run + Guid.NewGuid().ToString("N")).PadRight(40, 'x')[..40];
    var r = await Call("POST", "/v1/auth/device", new { deviceId, create = true });
    Check($"login {label}", r.Status == 200 && S(r.Json, "token") is not null, r.Body);
    return new Player(label, S(r.Json, "userId") ?? "", S(r.Json, "token") ?? "", S(r.Json, "username") ?? "");
}

// -------------------------------------------------------------------------------------------------------------

Log($"GameServer smoke test, run {run}, server {baseUrl}");

Player a = null!, b = null!, c = null!;
Rt? ra = null, rb = null, rc = null;
string groupId = "", partyId = "";
var transports = new List<string>();

await Section("server is up", async () =>
{
    var h = await Call("GET", "/healthz");
    Check("healthz 200", h.Status == 200, h.Body);
    var rpcs = h.Json is { } j && j.TryGetProperty("rpcs", out var arr) ? arr.GetRawText() : "";
    Info("rpcs: " + rpcs);
    Check("sample module loaded (rpc 'hello')", rpcs.Contains("hello"), "run: dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules");

    var rt = await Call("GET", "/v1/realtime");
    Check("GET /v1/realtime 200", rt.Status == 200, rt.Body);
    if (rt.Json is { } rj && rj.TryGetProperty("transports", out var ts))
        foreach (var t in ts.EnumerateArray())
            if (t.TryGetProperty("name", out var n) && n.GetString() is { } name) transports.Add(name);
    Info("transports: " + string.Join(", ", transports));
    Check("websocket transport enabled", transports.Contains("websocket"));
});

await Section("accounts and login hook", async () =>
{
    a = await NewPlayer("A");
    b = await NewPlayer("B");
    c = await NewPlayer("C");

    var acc = await Call("GET", "/v1/account", token: a.Token);
    Check("GET /v1/account", acc.Status == 200 && S(acc.Json, "id") == a.Id, acc.Body);

    var upd = await Call("PUT", "/v1/account", new { displayName = "Smoke A " + run }, a.Token);
    Check("PUT /v1/account (account.update hook path)", upd.Status == 200 && (S(upd.Json, "displayName") ?? "").StartsWith("Smoke A"), upd.Body);

    var wallet = await Call("GET", "/v1/wallet", token: a.Token);
    var coins = S(wallet.Json, "wallet", "coins");
    Info("wallet coins after login: " + (coins ?? "none"));
    Check("auth.login after-hook gave the welcome gift (sample module)", coins is not null && long.Parse(coins) >= 50,
        "needs the published sample module; coins=" + coins);

    var bad = await Call("GET", "/v1/account");
    Check("no token -> 401", bad.Status == 401);
});

await Section("realtime connect (websocket)", async () =>
{
    var wsUrl = new Uri(baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws");

    // Not authenticated: other messages are refused.
    var anon = await Rt.ConnectWs("anon", wsUrl);
    var r1 = await anon.Req("channel.join", new { channel = "room:x" });
    Check("message before auth -> error not_authenticated", r1 is not null && S(r1, "data", "code") == "not_authenticated", r1?.GetRawText());
    var pong = await anon.Req("ping", new { });
    Check("ping -> pong (no auth needed)", pong is not null && S(pong, "type") == "pong", pong?.GetRawText());
    await anon.Close();

    // Bad token: error, then the server closes.
    var bad = await Rt.ConnectWs("bad", wsUrl);
    var r2 = await bad.Req("auth", new { token = "not-a-token" });
    Check("bad token -> error unauthorized", r2 is not null && S(r2, "data", "code") == "unauthorized", r2?.GetRawText());
    await Task.Delay(300);
    Check("server closed the connection after bad auth", bad.Closed);
    await bad.Close();

    ra = await Rt.ConnectWs("A", wsUrl);
    rb = await Rt.ConnectWs("B", wsUrl);
    rc = await Rt.ConnectWs("C", wsUrl);
    foreach (var (rt, p) in new[] { (ra, a), (rb, b), (rc, c) })
    {
        var ok = await rt.Req("auth", new { token = p.Token });
        Check($"auth {p.Label} -> auth.ok", ok is not null && S(ok, "type") == "auth.ok" && S(ok, "data", "userId") == p.Id, ok?.GetRawText());
    }

    var online = await Call("GET", $"/v1/online?ids={a.Id},{b.Id},{c.Id}", token: a.Token);
    Check("GET /v1/online lists connected players", online.Status == 200 && (online.Body.Contains(a.Id) && online.Body.Contains(b.Id)), online.Body);
});

await Section("friends", async () =>
{
    var inv = await Call("POST", "/v1/friends", new { userId = b.Id }, a.Token);
    Check("A invites B -> invite_sent", inv.Status == 200 && S(inv.Json, "state") == "invite_sent", inv.Body);
    var ev = await rb!.Wait(f => S(f, "type") == "friend.request");
    Check("B got realtime event friend.request", ev is not null);

    var listB = await Call("GET", "/v1/friends?state=invite_received", token: b.Token);
    Check("B sees the invite", listB.Status == 200 && listB.Body.Contains(a.Id), listB.Body);

    var acc = await Call("POST", "/v1/friends", new { userId = a.Id }, b.Token);
    Check("B accepts by adding A -> friend", acc.Status == 200 && S(acc.Json, "state") == "friend", acc.Body);
    var ev2 = await ra!.Wait(f => S(f, "type") == "friend.accepted");
    Check("A got realtime event friend.accepted", ev2 is not null);

    var listA = await Call("GET", "/v1/friends?state=friend", token: a.Token);
    Check("A lists B as friend (online)", listA.Status == 200 && listA.Body.Contains(b.Id), listA.Body);

    var self = await Call("POST", "/v1/friends", new { userId = a.Id }, a.Token);
    Check("cannot add yourself -> 400", self.Status == 400, self.Body);

    var blk = await Call("POST", $"/v1/friends/{c.Id}/block", token: a.Token);
    Check("A blocks C -> 204", blk.Status == 204, blk.Body);
    var invBlocked = await Call("POST", "/v1/friends", new { userId = a.Id }, c.Token);
    // By design the blocked player is not told: the call looks like a normal invite, but nothing is stored.
    Check("C invites A (blocked): looks normal to C", invBlocked.Status == 200 && invBlocked.Body.Contains("invite_sent"), invBlocked.Body);
    var aInvites = await Call("GET", "/v1/friends?state=invite_received", token: a.Token);
    Check("...but A gets no invite from C", aInvites.Status == 200 && !aInvites.Body.Contains(c.Id), aInvites.Body);
    var unb = await Call("DELETE", $"/v1/friends/{c.Id}/block", token: a.Token);
    Check("A unblocks C -> 204", unb.Status == 204, unb.Body);
});

await Section("groups / clans", async () =>
{
    var name = "smoke" + run;
    var create = await Call("POST", "/v1/groups", new { name, description = "smoke test clan", type = "clan", open = true }, a.Token);
    groupId = S(create.Json, "group", "id") ?? "";
    Check("A creates clan", create.Status == 200 && groupId.Length > 0, create.Body);

    var second = await Call("POST", "/v1/groups", new { name = name + "2", type = "clan" }, a.Token);
    Check("second clan for A -> group_limit_reached (409)", second.Status == 409 && Err(second) == "group_limit_reached", second.Body);

    var dup = await Call("POST", "/v1/groups", new { name, type = "group" }, b.Token);
    Check("duplicate group name -> 409", dup.Status == 409, dup.Body);

    var search = await Call("GET", $"/v1/groups?q={name}", token: b.Token);
    Check("search finds the clan", search.Status == 200 && search.Body.Contains(groupId), search.Body);

    var joinB = await Call("POST", $"/v1/groups/{groupId}/join", token: b.Token);
    Check("B joins open clan as member", joinB.Status == 200 && S(joinB.Json, "status") == "member", joinB.Body);
    var joinC = await Call("POST", $"/v1/groups/{groupId}/join", token: c.Token);
    Check("C joins open clan", joinC.Status == 200, joinC.Body);

    var members = await Call("GET", $"/v1/groups/{groupId}/members", token: a.Token);
    Check("members list has A, B, C", members.Status == 200 && members.Body.Contains(a.Id) && members.Body.Contains(b.Id) && members.Body.Contains(c.Id), members.Body);

    var promote = await Call("POST", $"/v1/groups/{groupId}/members/{b.Id}/promote", token: a.Token);
    Check("A promotes B to admin", promote.Status == 204, promote.Body);
    var kickDenied = await Call("POST", $"/v1/groups/{groupId}/members/{a.Id}/kick", token: b.Token);
    Check("B (admin) cannot kick the owner -> 403", kickDenied.Status == 403, kickDenied.Body);

    var kick = await Call("POST", $"/v1/groups/{groupId}/members/{c.Id}/kick", token: a.Token);
    Check("A kicks C", kick.Status == 204, kick.Body);
    var rejoin = await Call("POST", $"/v1/groups/{groupId}/join", token: c.Token);
    Check("C can rejoin after a kick", rejoin.Status == 200, rejoin.Body);
    var ban = await Call("POST", $"/v1/groups/{groupId}/members/{c.Id}/ban", token: a.Token);
    Check("A bans C", ban.Status == 204, ban.Body);
    var rejoin2 = await Call("POST", $"/v1/groups/{groupId}/join", token: c.Token);
    Check("banned C cannot join -> 403 banned_from_group", rejoin2.Status == 403 && Err(rejoin2) == "banned_from_group", rejoin2.Body);
    var unban = await Call("DELETE", $"/v1/groups/{groupId}/members/{c.Id}/ban", token: a.Token);
    Check("A unbans C", unban.Status == 204, unban.Body);

    var mine = await Call("GET", "/v1/groups/mine", token: b.Token);
    Check("B's groups include the clan", mine.Status == 200 && mine.Body.Contains(groupId), mine.Body);

    var upd = await Call("PUT", $"/v1/groups/{groupId}", new { description = "changed" }, a.Token);
    Check("A updates description", upd.Status == 200 && S(upd.Json, "group", "description") == "changed", upd.Body);
});

await Section("chat: rooms, groups, direct messages (websocket)", async () =>
{
    // Room chat
    var j1 = await ra!.Req("channel.join", new { channel = "room:smoke" });
    var j2 = await rb!.Req("channel.join", new { channel = "room:smoke" });
    Check("A joins room", j1 is not null && S(j1, "type") == "channel.joined", j1?.GetRawText());
    Check("B joins room", j2 is not null && S(j2, "type") == "channel.joined", j2?.GetRawText());

    var sent = await ra.Req("channel.send", new { channel = "room:smoke", text = "hello room " + run });
    Check("A sends -> channel.sent", sent is not null && S(sent, "type") == "channel.sent", sent?.GetRawText());
    var got = await rb.Wait(f => S(f, "type") == "channel.message" && (S(f, "data", "text") ?? "").Contains("hello room " + run));
    Check("B receives the room message", got is not null);

    var noJoin = await ra.Req("channel.send", new { channel = "room:not-joined", text = "x" });
    Check("send without join -> not_in_channel", noJoin is not null && S(noJoin, "data", "code") == "not_in_channel", noJoin?.GetRawText());

    var badChan = await ra.Req("channel.join", new { channel = "nonsense" });
    Check("invalid channel -> invalid_channel", badChan is not null && S(badChan, "data", "code") == "invalid_channel", badChan?.GetRawText());

    var empty = await ra.Req("channel.send", new { channel = "room:smoke", text = "   " });
    Check("empty message rejected", empty is not null && S(empty, "data", "code") == "invalid_message", empty?.GetRawText());

    var link = await ra.Req("channel.send", new { channel = "room:smoke", text = "see http://example.com" });
    Info("before-hook on chat.send (sample module blocks links): " + link?.GetRawText());
    Check("chat.send before-hook blocked a link (sample module)", link is not null && S(link, "data", "code") == "invalid_message", link?.GetRawText());

    // Group chat (stored)
    await ra.Req("channel.join", new { channel = "group:" + groupId });
    await rb.Req("channel.join", new { channel = "group:" + groupId });
    var gs = await ra.Req("channel.send", new { channel = "group:" + groupId, text = "hello clan " + run });
    Check("A sends to the clan channel", gs is not null && S(gs, "type") == "channel.sent", gs?.GetRawText());
    var gm = await rb.Wait(f => S(f, "type") == "channel.message" && (S(f, "data", "text") ?? "").Contains("hello clan " + run));
    Check("B receives the clan message", gm is not null);

    var hist = await Call("GET", $"/v1/chat/history?channel=group:{groupId}&limit=10", token: b.Token);
    Check("clan history stored (REST)", hist.Status == 200 && hist.Body.Contains("hello clan " + run), hist.Body);
    var hws = await rb.Req("channel.history", new { channel = "group:" + groupId, limit = 10 });
    Check("clan history (websocket)", hws is not null && (hws.Value.GetRawText()).Contains("hello clan " + run), hws?.GetRawText());

    var outsider = await rc!.Req("channel.join", new { channel = "group:" + groupId });
    Check("non-member C cannot join clan channel -> not_a_member", outsider is not null && S(outsider, "data", "code") == "not_a_member", outsider?.GetRawText());

    // Direct message
    var dm = await ra.Req("channel.send", new { channel = "dm:" + b.Id, text = "psst " + run });
    Check("A sends a direct message", dm is not null && S(dm, "type") == "channel.sent", dm?.GetRawText());
    var dmGot = await rb.Wait(f => S(f, "type") == "channel.message" && (S(f, "data", "text") ?? "").Contains("psst " + run));
    Check("B receives the direct message", dmGot is not null);
    var dmHist = await Call("GET", $"/v1/chat/history?channel=dm:{a.Id}&limit=10", token: b.Token);
    Check("B reads the direct message history", dmHist.Status == 200 && dmHist.Body.Contains("psst " + run), dmHist.Body);

    var leave = await rb.Req("channel.leave", new { channel = "room:smoke" });
    Check("B leaves the room", leave is not null && S(leave, "type") == "channel.left", leave?.GetRawText());
});

await Section("parties", async () =>
{
    var create = await Call("POST", "/v1/parties", new { open = false, maxSize = 4 }, a.Token);
    partyId = S(create.Json, "party", "id") ?? "";
    Check("A creates a party (needs a connection)", create.Status == 200 && partyId.Length > 0, create.Body);

    var notInvited = await Call("POST", $"/v1/parties/{partyId}/join", token: b.Token);
    Check("B cannot join a closed party uninvited -> 403", notInvited.Status == 403, notInvited.Body);

    var invite = await Call("POST", $"/v1/parties/invite/{b.Id}", token: a.Token);
    Check("A invites B", invite.Status == 204, invite.Body);
    var ev = await rb!.Wait(f => S(f, "type") == "party.invite");
    Check("B got realtime event party.invite", ev is not null);

    var join = await Call("POST", $"/v1/parties/{partyId}/join", token: b.Token);
    Check("B joins the party", join.Status == 200, join.Body);
    var upd = await ra!.Wait(f => S(f, "type") == "party.update");
    Check("A got realtime event party.update", upd is not null);

    await ra.Req("channel.join", new { channel = "party:" + partyId });
    await rb.Req("channel.join", new { channel = "party:" + partyId });
    await ra.Req("channel.send", new { channel = "party:" + partyId, text = "party chat " + run });
    var pm = await rb.Wait(f => S(f, "type") == "channel.message" && (S(f, "data", "text") ?? "").Contains("party chat " + run));
    Check("party chat reaches B", pm is not null);

    var outsider = await rc!.Req("channel.join", new { channel = "party:" + partyId });
    Check("C cannot join party chat -> not_a_member", outsider is not null && S(outsider, "data", "code") == "not_a_member", outsider?.GetRawText());

    var mineB = await Call("GET", "/v1/parties/mine", token: b.Token);
    Check("B sees own party", mineB.Status == 200 && mineB.Body.Contains(partyId), mineB.Body);

    var leave = await Call("POST", "/v1/parties/leave", token: b.Token);
    Check("B leaves", leave.Status == 204, leave.Body);
    var close = await Call("DELETE", "/v1/parties/mine", token: a.Token);
    Check("A closes the party (leader)", close.Status == 204, close.Body);
});

await Section("stats, achievements, quests, daily reward", async () =>
{
    var bad = await Call("PUT", "/admin/api/config/achievements", new { value = new { achievements = new { broken = new { name = "no stat", target = 1 } } } }, admin: true);
    Check("an achievement without 'stat' is refused -> 400", bad.Status == 400, bad.Body);

    var a1 = await Call("PUT", "/admin/api/config/achievements", new
    {
        value = new { achievements = new { smoke_win = new { name = "Three wins", stat = "smoke_wins", target = 3, reward = new { wallet = new { smokegems = 7 } } } } },
        @public = false
    }, admin: true);
    var q1 = await Call("PUT", "/admin/api/config/quests", new
    {
        value = new { quests = new { smoke_played = new { name = "Play twice", stat = "smoke_played", target = 2, period = "day", reward = new { wallet = new { smokegold = 20 } } } } },
        @public = false
    }, admin: true);
    var d1 = await Call("PUT", "/admin/api/config/daily_rewards", new
    {
        value = new { resetOnMiss = true, rewards = new object[] { new { wallet = new { smokedaily = 10 } }, new { wallet = new { smokedaily = 20 } } } },
        @public = false
    }, admin: true);
    Check("admin writes achievements, quests and daily_rewards", a1.Status == 200 && q1.Status == 200 && d1.Status == 200, a1.Body + q1.Body + d1.Body);

    var list = await Call("GET", "/v1/achievements", token: b.Token);
    Check("GET /v1/achievements shows progress 0", list.Status == 200 && list.Body.Contains("smoke_win") && list.Body.Contains("\"progress\":0"), list.Body);

    await Call("POST", $"/admin/api/users/{b.Id}/stats", new { stat = "smoke_wins", value = 2 }, admin: true);
    var early = await Call("POST", "/v1/achievements/smoke_win/claim", null, b.Token);
    Check("claiming before the target -> 409 goal_not_complete", early.Status == 409 && early.Body.Contains("goal_not_complete"), early.Body);

    await Call("POST", $"/admin/api/users/{b.Id}/stats", new { stat = "smoke_wins", value = 1 }, admin: true);
    var ev = await rb!.Wait(f => S(f, "type") == "goal.completed" && S(f, "data", "id") == "smoke_win", 4000);
    Check("reaching the target pushes the realtime event goal.completed", ev is not null);

    var claim = await Call("POST", "/v1/achievements/smoke_win/claim", null, b.Token);
    Check("claim the achievement reward", claim.Status == 200 && S(claim.Json, "claim", "state", "wallet", "smokegems") == "7", claim.Body);
    var again = await Call("POST", "/v1/achievements/smoke_win/claim", null, b.Token);
    Check("claiming twice -> 409 already_claimed", again.Status == 409 && again.Body.Contains("already_claimed"), again.Body);

    await Call("POST", $"/admin/api/users/{b.Id}/stats", new { stat = "smoke_played", value = 2 }, admin: true);
    var qc = await Call("POST", "/v1/quests/smoke_played/claim", null, b.Token);
    Check("claim the daily quest reward", qc.Status == 200 && S(qc.Json, "claim", "state", "wallet", "smokegold") == "20", qc.Body);
    var qc2 = await Call("POST", "/v1/quests/smoke_played/claim", null, b.Token);
    Check("the quest cannot be claimed twice in one day", qc2.Status == 409, qc2.Body);

    var stats = await Call("GET", "/v1/stats", token: b.Token);
    Check("GET /v1/stats has the lifetime value", stats.Status == 200 && S(stats.Json, "stats", "smoke_wins") == "3", stats.Body);

    await Call("POST", $"/admin/api/users/{b.Id}/stats", new { stat = "smoke_best", value = 50, mode = "max" }, admin: true);
    var low = await Call("POST", $"/admin/api/users/{b.Id}/stats", new { stat = "smoke_best", value = 30, mode = "max" }, admin: true);
    Check("a stat set to max never goes down", low.Status == 200 && S(low.Json, "lifetime") == "50", low.Body);

    var daily = await Call("GET", "/v1/daily", token: b.Token);
    Check("GET /v1/daily: nothing claimed yet, next is day 1",
        daily.Status == 200 && S(daily.Json, "daily", "claimedToday") == "false" && S(daily.Json, "daily", "nextDay") == "1", daily.Body);
    var dc = await Call("POST", "/v1/daily/claim", null, b.Token);
    Check("claim the daily reward (day 1, streak 1)",
        dc.Status == 200 && S(dc.Json, "claim", "day") == "1" && S(dc.Json, "claim", "streak") == "1"
        && S(dc.Json, "claim", "state", "wallet", "smokedaily") == "10", dc.Body);
    var dc2 = await Call("POST", "/v1/daily/claim", null, b.Token);
    Check("a second claim today -> 409 daily_already_claimed", dc2.Status == 409 && dc2.Body.Contains("daily_already_claimed"), dc2.Body);
    var daily2 = await Call("GET", "/v1/daily", token: b.Token);
    Check("after claiming: claimedToday, next reward is day 2",
        S(daily2.Json, "daily", "claimedToday") == "true" && S(daily2.Json, "daily", "nextDay") == "2", daily2.Body);
});

await Section("shop", async () =>
{
    var bad = await Call("PUT", "/admin/api/config/shop", new { value = new { products = new { broken = new { name = "no gives" } } } }, admin: true);
    Check("a product without 'gives' is refused -> 400", bad.Status == 400, bad.Body);

    var set = await Call("PUT", "/admin/api/config/shop", new
    {
        value = new
        {
            products = new Dictionary<string, object>
            {
                ["smoke_pack"] = new { name = "Smoke pack", category = "smoke", cost = new { wallet = new { smokegold = 100 } },
                    gives = new { wallet = new { smokegems = 10 } }, limit = new { per = "day", count = 2 } },
                ["smoke_free"] = new { name = "Free gift", category = "smoke", gives = new { items = new { smoke_gift = 1 } },
                    limit = new { per = "lifetime", count = 1 } },
                ["smoke_later"] = new { name = "Not yet", gives = new { wallet = new { smokegems = 1 } },
                    startUtc = DateTime.UtcNow.AddDays(30).ToString("o") },
            }
        },
        @public = false
    }, admin: true);
    Check("admin writes the 'shop' config", set.Status == 200, set.Body);

    var grant = await Call("POST", $"/admin/api/users/{a.Id}/economy", new { wallet = new { smokegold = 250 }, reason = "smoke test" }, admin: true);
    Check("admin gives A 250 smokegold", grant.Status == 200, grant.Body);

    var list = await Call("GET", "/v1/shop?category=smoke", token: a.Token);
    Check("GET /v1/shop lists the products", list.Status == 200 && list.Body.Contains("smoke_pack") && list.Body.Contains("smoke_free"), list.Body);
    Check("a future product is listed as not active", list.Body.Contains("\"active\":false") || !list.Body.Contains("smoke_later"), list.Body);

    var rid = "r" + run;
    var buy1 = await Call("POST", "/v1/shop/smoke_pack/buy", new { requestId = rid }, a.Token);
    Check("A buys smoke_pack", buy1.Status == 200 && S(buy1.Json, "purchase", "state", "wallet", "smokegold") == "150"
        && S(buy1.Json, "purchase", "state", "wallet", "smokegems") == "10", buy1.Body);

    var retry = await Call("POST", "/v1/shop/smoke_pack/buy", new { requestId = rid }, a.Token);
    Check("same requestId again is applied only once", retry.Status == 200 && S(retry.Json, "purchase", "alreadyApplied") == "true"
        && S(retry.Json, "purchase", "state", "wallet", "smokegold") == "150", retry.Body);

    var buy2 = await Call("POST", "/v1/shop/smoke_pack/buy", new { requestId = rid + "b" }, a.Token);
    Check("A buys it a second time (limit 2 per day)", buy2.Status == 200 && S(buy2.Json, "purchase", "state", "wallet", "smokegold") == "50", buy2.Body);
    var buy3 = await Call("POST", "/v1/shop/smoke_pack/buy", new { requestId = rid + "c" }, a.Token);
    Check("third purchase -> 409 purchase_limit_reached", buy3.Status == 409 && buy3.Body.Contains("purchase_limit_reached"), buy3.Body);

    var free1 = await Call("POST", "/v1/shop/smoke_free/buy", null, a.Token);
    var free2 = await Call("POST", "/v1/shop/smoke_free/buy", null, a.Token);
    Check("a free product with a lifetime limit works once", free1.Status == 200 && free2.Status == 409, free1.Body + free2.Body);

    var later = await Call("POST", "/v1/shop/smoke_later/buy", null, a.Token);
    Check("a product that is not on sale yet -> 409 product_unavailable", later.Status == 409 && later.Body.Contains("product_unavailable"), later.Body);
    var nope = await Call("POST", "/v1/shop/doesnotexist/buy", null, a.Token);
    Check("unknown product -> 404", nope.Status == 404, nope.Body);

    var poor = await Call("POST", "/v1/shop/smoke_pack/buy", null, b.Token);
    Check("B has no smokegold -> 409 insufficient_funds", poor.Status == 409 && poor.Body.Contains("insufficient_funds"), poor.Body);
    var listB = await Call("GET", "/v1/shop?category=smoke", token: b.Token);
    Check("a failed purchase does not use up B's limit", listB.Status == 200 && listB.Body.Contains("\"remaining\":2"), listB.Body);

    var adminView = await Call("GET", $"/admin/api/users/{a.Id}/shop", admin: true);
    Check("admin sees A's purchases", adminView.Status == 200 && adminView.Body.Contains("smoke_pack"), adminView.Body);
});

await Section("live notification event", async () =>
{
    var sent = await Call("POST", $"/admin/api/users/{a.Id}/notifications", new { subject = "smoke " + run, body = "hello" }, admin: true);
    Check("admin sends a notification", sent.Status == 200, sent.Body);
    var ev = await ra!.Wait(f => S(f, "type") == "notification.new", 4000);
    Check("online player gets the realtime event notification.new", ev is not null && S(ev, "data", "subject") == "smoke " + run, ev?.GetRawText());
});

await Section("matchmaker and matches (websocket)", async () =>
{
    var set = await Call("PUT", "/admin/api/config/matches", new
    {
        value = new
        {
            modes = new Dictionary<string, object>
            {
                ["smoke_duel"] = new { handler = "relay", transport = "websocket", minPlayers = 2, maxPlayers = 2, fillSeconds = 2, emptyTimeoutSeconds = 5,
                    rules = new { region = "equal" } },
                ["smoke_open"] = new { handler = "relay", minPlayers = 2, maxPlayers = 4, allowClientCreate = true, emptyTimeoutSeconds = 5 },
            }
        },
        @public = false
    }, admin: true);
    Check("admin writes the 'matches' config", set.Status == 200, set.Body);
    var bad = await Call("PUT", "/admin/api/config/matches", new { value = new { modes = new { x = new { minPlayers = 5, maxPlayers = 2 } } } }, admin: true);
    Check("invalid 'matches' config is refused -> 400", bad.Status == 400, bad.Body);
    Info("waiting for the mode cache (3 s)");
    await Task.Delay(3500);

    var modes = await Call("GET", "/v1/match/modes");
    Check("GET /v1/match/modes lists the modes", modes.Status == 200 && modes.Body.Contains("smoke_duel") && modes.Body.Contains("smoke_open"), modes.Body);

    // ---- matchmaker: A and B (same region) are matched, C (other region) is not ----
    var tc = await Call("POST", "/v1/matchmaker/tickets", new { mode = "smoke_duel", properties = new { region = "us" } }, c.Token);
    Check("C queues (region us)", tc.Status == 200, tc.Body);
    var dup = await Call("POST", "/v1/matchmaker/tickets", new { mode = "smoke_duel", properties = new { region = "us" } }, c.Token);
    Check("C queueing twice -> 409 already_queued", dup.Status == 409, dup.Body);
    var noProp = await Call("POST", "/v1/matchmaker/tickets", new { mode = "smoke_duel" }, a.Token);
    Check("missing rule property -> 400", noProp.Status == 400, noProp.Body);

    var ta = await Call("POST", "/v1/matchmaker/tickets", new { mode = "smoke_duel", properties = new { region = "eu" } }, a.Token);
    var tb = await Call("POST", "/v1/matchmaker/tickets", new { mode = "smoke_duel", properties = new { region = "eu" } }, b.Token);
    Check("A and B queue (region eu)", ta.Status == 200 && tb.Status == 200, ta.Body + tb.Body);

    var ma = await ra!.Wait(f => S(f, "type") == "matchmaker.matched", 8000);
    var mb = await rb!.Wait(f => S(f, "type") == "matchmaker.matched", 8000);
    Check("A and B got matchmaker.matched", ma is not null && mb is not null);
    var cMatched = await rc!.Wait(f => S(f, "type") == "matchmaker.matched", 1500);
    Check("C (other region) was not matched", cMatched is null);
    if (ma is null || mb is null) return;

    var matchId = S(ma, "data", "matchId") ?? "";
    Check("both got the same match, transport websocket", matchId == S(mb, "data", "matchId") && S(ma, "data", "transport") == "websocket", ma.Value.GetRawText());

    var matchUrl = new Uri(baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/match?token=");
    var badConn = await MatchClient.TryConnect(new Uri(matchUrl + "wrong"));
    Check("a wrong join token is refused", badConn is null);

    var ca = await MatchClient.TryConnect(new Uri(matchUrl + Uri.EscapeDataString(S(ma, "data", "token")!)));
    var cb = await MatchClient.TryConnect(new Uri(matchUrl + Uri.EscapeDataString(S(mb, "data", "token")!)));
    Check("A and B connect to the match with their tokens", ca is not null && cb is not null);
    if (ca is null || cb is null) return;

    var again = await MatchClient.TryConnect(new Uri(matchUrl + Uri.EscapeDataString(S(ma, "data", "token")!)));
    Check("a join token cannot be used twice", again is null);

    await ca.Send(new byte[] { 1, 2, 3 });
    var got = await cb.Receive();
    Check("relay: B receives A's bytes prefixed with A's slot", got is { Length: 4 } && got[1] == 1 && got[2] == 2 && got[3] == 3, got is null ? "nothing" : Convert.ToHexString(got));

    var info = await Call("GET", $"/admin/api/matches", admin: true);
    Check("admin lists the running match", info.Status == 200 && info.Body.Contains(matchId), info.Body);

    var end = await Call("DELETE", $"/admin/api/matches/{matchId}", admin: true);
    Check("admin ends the match", end.Status == 204, end.Body);
    Check("players' connections are closed", await ca.WaitClosed() && await cb.WaitClosed());

    var cancel = await Call("GET", "/v1/matchmaker/tickets", token: c.Token);
    var tid = S(cancel.Json, "tickets") is { } ? cancel.Json!.Value.GetProperty("tickets")[0].GetProperty("id").GetString() : null;
    var del = await Call("DELETE", $"/v1/matchmaker/tickets/{tid}", token: c.Token);
    Check("C leaves the queue", del.Status == 204, del.Body);

    // ---- player-created match ----
    var forbidden = await Call("POST", "/v1/matches", new { mode = "smoke_duel" }, a.Token);
    Check("creating a matchmaker-only mode -> 403", forbidden.Status == 403, forbidden.Body);
    var made = await Call("POST", "/v1/matches", new { mode = "smoke_open", label = "smoke " + run }, a.Token);
    var openId = S(made.Json, "match", "id") ?? "";
    Check("A creates an open match and gets a join token", made.Status == 200 && openId.Length > 0 && S(made.Json, "connect", "token") is not null, made.Body);
    var list = await Call("GET", "/v1/matches?mode=smoke_open", token: b.Token);
    Check("B finds it in the list", list.Status == 200 && list.Body.Contains(openId), list.Body);
    var joined = await Call("POST", $"/v1/matches/{openId}/join", token: b.Token);
    Check("B joins and gets a join token", joined.Status == 200 && S(joined.Json, "connect", "token") is not null, joined.Body);
    var gone = await Call("POST", "/v1/matches/doesnotexist/join", token: b.Token);
    Check("joining an unknown match -> 404", gone.Status == 404, gone.Body);
    await Call("DELETE", $"/admin/api/matches/{openId}", admin: true);

    // ---- the same thing over the realtime socket ----
    var viaSocket = await ra.Req("match.create", new { mode = "smoke_open" });
    Check("match.create over the socket", viaSocket is not null && S(viaSocket, "type") == "match.created", viaSocket?.GetRawText());
    if (S(viaSocket, "data", "match", "id") is { } sockId)
        await Call("DELETE", $"/admin/api/matches/{sockId}", admin: true);

    if (transports.Count > 0 && modes.Body.Contains("\"udp\""))
        Info("udp is enabled on the server: test it with a LiteNetLib client (send the join token as the connection request string).");
});

await Section("tcp transport", async () =>
{
    if (!transports.Contains("tcp")) { Info("tcp transport not enabled - skipped (set GameServer__Realtime__Transports=websocket,tcp)"); return; }
    var host = new Uri(baseUrl).Host;
    var t = await Rt.ConnectTcp("A-tcp", host, 7351);
    var ok = await t.Req("auth", new { token = a.Token });
    Check("tcp: auth -> auth.ok", ok is not null && S(ok, "type") == "auth.ok" && S(ok, "data", "transport") == "tcp", ok?.GetRawText());
    var pong = await t.Req("ping", new { });
    Check("tcp: ping -> pong", pong is not null && S(pong, "type") == "pong", pong?.GetRawText());
    var j = await t.Req("channel.join", new { channel = "room:smoke" });
    Check("tcp: join room", j is not null && S(j, "type") == "channel.joined", j?.GetRawText());
    var s = await t.Req("channel.send", new { channel = "room:smoke", text = "from tcp " + run });
    Check("tcp: send", s is not null && S(s, "type") == "channel.sent", s?.GetRawText());
    var got = await rb!.Wait(f => S(f, "type") == "channel.message" && (S(f, "data", "text") ?? "").Contains("from tcp " + run), 1500);
    Info("websocket client B (left the room) got the tcp message: " + (got is not null));
    await t.Close();
});

await Section("admin API", async () =>
{
    var wrong = await Call("GET", "/admin/api/users?limit=1");
    Check("admin without key -> 403", wrong.Status == 403, wrong.Body);
    var users = await Call("GET", "/admin/api/users?limit=1", admin: true);
    Check("admin with key -> 200", users.Status == 200, users.Body);

    var groups = await Call("GET", $"/admin/api/groups?q=smoke{run}", admin: true);
    Check("admin finds the clan", groups.Status == 200 && groups.Body.Contains(groupId), groups.Body);
    var g = await Call("GET", $"/admin/api/groups/{groupId}", admin: true);
    Check("admin group detail lists members (incl. banned/pending)", g.Status == 200 && g.Body.Contains(a.Id) && g.Body.Contains(b.Id), g.Body);
    var gu = await Call("PUT", $"/admin/api/groups/{groupId}", new { description = "set by admin" }, admin: true);
    Check("admin edits the group", gu.Status == 200 && S(gu.Json, "group", "description") == "set by admin", gu.Body);

    var friends = await Call("GET", $"/admin/api/users/{a.Id}/friends", admin: true);
    Check("admin sees A's friends", friends.Status == 200 && friends.Body.Contains(b.Id), friends.Body);
    var ug = await Call("GET", $"/admin/api/users/{b.Id}/groups", admin: true);
    Check("admin sees B's groups", ug.Status == 200 && ug.Body.Contains(groupId), ug.Body);

    var channels = await Call("GET", "/admin/api/chat/channels", admin: true);
    Check("admin lists chat channels", channels.Status == 200 && channels.Body.Contains("group:" + groupId), channels.Body);
    var msgs = await Call("GET", $"/admin/api/chat/messages?channel=group:{groupId}", admin: true);
    Check("admin reads clan messages", msgs.Status == 200 && msgs.Body.Contains("hello clan " + run), msgs.Body);
    var msgId = msgs.Json is { } mj && mj.TryGetProperty("items", out var items) && items.GetArrayLength() > 0
        ? items[0].GetProperty("id").GetInt64() : 0;
    var sys = await Call("POST", "/admin/api/chat/send", new { channel = "group:" + groupId, text = "system hello " + run }, admin: true);
    Check("admin sends a system message", sys.Status == 204, sys.Body);
    var sysGot = await rb!.Wait(f => S(f, "type") == "channel.message" && (S(f, "data", "text") ?? "").Contains("system hello " + run));
    Check("B receives the system message", sysGot is not null);
    if (msgId > 0)
    {
        var del = await Call("DELETE", $"/admin/api/chat/messages/{msgId}", admin: true);
        Check("admin deletes a message", del.Status == 204, del.Body);
        var delEv = await rb.Wait(f => S(f, "type") == "channel.message_deleted");
        Check("B got channel.message_deleted", delEv is not null);
    }

    var online = await Call("GET", "/admin/api/online", admin: true);
    Check("admin online list shows connections", online.Status == 200 && online.Body.Contains(a.Id), online.Body);

    // Mute
    var mute = await Call("POST", $"/admin/api/users/{b.Id}/mute", new { minutes = 5, reason = "smoke test" }, admin: true);
    Check("admin mutes B", mute.Status == 204, mute.Body);
    var muted = await rb.Req("channel.send", new { channel = "group:" + groupId, text = "muted?" });
    Check("muted B cannot send -> muted", muted is not null && S(muted, "data", "code") == "muted", muted?.GetRawText());
    var mutes = await Call("GET", "/admin/api/chat/mutes", admin: true);
    Check("mute list shows B", mutes.Status == 200 && mutes.Body.Contains(b.Id), mutes.Body);
    var unmute = await Call("DELETE", $"/admin/api/users/{b.Id}/mute", admin: true);
    Check("admin unmutes B", unmute.Status == 204, unmute.Body);
    var after = await rb.Req("channel.send", new { channel = "group:" + groupId, text = "back " + run });
    Check("unmuted B can send again", after is not null && S(after, "type") == "channel.sent", after?.GetRawText());

    // Transfer ownership, then ban/disconnect
    var tr = await Call("POST", $"/admin/api/groups/{groupId}/transfer/{b.Id}", admin: true);
    Check("admin transfers ownership to B", tr.Status == 204, tr.Body);

    var disc = await Call("POST", $"/admin/api/users/{c.Id}/disconnect", admin: true);
    Check("admin disconnects C", disc.Status == 204, disc.Body);
    await Task.Delay(500);
    Check("C's websocket is closed", rc!.Closed);

    var banU = await Call("POST", $"/admin/api/users/{c.Id}/ban", admin: true);
    Check("admin bans C", banU.Status == 204, banU.Body);
    var asBanned = await Call("GET", "/v1/account", token: c.Token);
    Check("banned C is refused -> 401", asBanned.Status == 401, asBanned.Body);
    var unbanU = await Call("POST", $"/admin/api/users/{c.Id}/unban", admin: true);
    Check("admin unbans C", unbanU.Status == 204, unbanU.Body);
});

await Section("admin console (roles, audit, dashboard, storage, live-ops, modules)", async () =>
{
    async Task<Resp> As(string key, string method, string path, object? body = null)
    {
        var saved = adminKey;
        adminKey = key;
        try { return await Call(method, path, body, admin: true); }
        finally { adminKey = saved; }
    }

    async Task<bool> Eventually(Func<Task<bool>> test, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await test()) return true;
            await Task.Delay(700);
        }
        return await test();
    }

    // ---- roles ----
    var me = await Call("GET", "/admin/api/me", admin: true);
    Check("/me: the master key is root with the Admin role", me.Status == 200 && S(me.Json, "name") == "root" && S(me.Json, "role") == "Admin", me.Body);

    var supName = "smokesup" + run;
    var roName = "smokero" + run;
    var mkSup = await Call("POST", "/admin/api/admins", new { name = supName, role = "Support" }, admin: true);
    var supKey = S(mkSup.Json, "key") ?? "";
    var supId = S(mkSup.Json, "account", "id") ?? "";
    Check("admin creates a Support admin and gets a key", mkSup.Status == 200 && supKey.StartsWith("gsk_") && supId.Length > 0, mkSup.Body);
    var mkRo = await Call("POST", "/admin/api/admins", new { name = roName, role = "ReadOnly" }, admin: true);
    var roKey = S(mkRo.Json, "key") ?? "";
    Check("admin creates a ReadOnly admin", mkRo.Status == 200 && roKey.Length > 0, mkRo.Body);
    var dup = await Call("POST", "/admin/api/admins", new { name = supName, role = "Support" }, admin: true);
    Check("a duplicate admin name is refused", dup.Status >= 400, dup.Body);
    var badName = await Call("POST", "/admin/api/admins", new { name = "root", role = "Admin" }, admin: true);
    Check("the name 'root' is reserved", badName.Status >= 400, badName.Body);

    var supMe = await As(supKey, "GET", "/admin/api/me");
    Check("Support key authenticates as Support", supMe.Status == 200 && S(supMe.Json, "role") == "Support" && S(supMe.Json, "name") == supName, supMe.Body);
    var supRead = await As(supKey, "GET", "/admin/api/users?limit=1");
    Check("Support can read users", supRead.Status == 200, supRead.Body);
    var supNotify = await As(supKey, "POST", $"/admin/api/users/{a.Id}/notifications", new { subject = "support hello " + run });
    Check("Support can send a notification", supNotify.Status == 200, supNotify.Body);
    var supConfig = await As(supKey, "PUT", "/admin/api/config/smoke_denied", new { value = new { x = 1 } });
    Check("Support cannot write config -> insufficient_role", supConfig.Status == 403 && supConfig.Body.Contains("insufficient_role"), supConfig.Body);
    var supAdmins = await As(supKey, "GET", "/admin/api/admins");
    Check("Support cannot list admins", supAdmins.Status == 403, supAdmins.Body);
    var supEconomy = await As(supKey, "POST", $"/admin/api/users/{a.Id}/economy", new { wallet = new { smokegold = 1 }, reason = "x" });
    Check("Support cannot change the wallet", supEconomy.Status == 403, supEconomy.Body);

    var roRead = await As(roKey, "GET", "/admin/api/dashboard");
    Check("ReadOnly can read the dashboard", roRead.Status == 200, roRead.Body);
    var roWrite = await As(roKey, "POST", $"/admin/api/users/{a.Id}/notifications", new { subject = "nope" });
    Check("ReadOnly cannot write -> 403", roWrite.Status == 403 && roWrite.Body.Contains("insufficient_role"), roWrite.Body);
    var roAudit = await As(roKey, "GET", "/admin/api/audit");
    Check("ReadOnly cannot read the audit log", roAudit.Status == 403, roAudit.Body);
    var unknown = await As("gsk_not_a_real_key_0000000000000000", "GET", "/admin/api/me");
    Check("an unknown key -> 403 forbidden", unknown.Status == 403 && unknown.Body.Contains("\"forbidden\""), unknown.Body);

    var disable = await Call("PUT", $"/admin/api/admins/{supId}", new { disabled = true }, admin: true);
    var supOff = await As(supKey, "GET", "/admin/api/me");
    Check("a disabled admin cannot log in", disable.Status == 204 && supOff.Status == 403, disable.Body + supOff.Body);
    var enable = await Call("PUT", $"/admin/api/admins/{supId}", new { disabled = false, role = "ReadOnly" }, admin: true);
    var supNow = await As(supKey, "GET", "/admin/api/me");
    Check("re-enabled and changed to ReadOnly", enable.Status == 204 && S(supNow.Json, "role") == "ReadOnly", supNow.Body);
    var reset = await Call("POST", $"/admin/api/admins/{supId}/reset-key", admin: true);
    var newKey = S(reset.Json, "key") ?? "";
    var oldKey = await As(supKey, "GET", "/admin/api/me");
    var freshKey = await As(newKey, "GET", "/admin/api/me");
    Check("a new key replaces the old one", newKey.Length > 0 && oldKey.Status == 403 && freshKey.Status == 200, reset.Body);

    // ---- audit log (written just after the response, so poll) ----
    var seen = await Eventually(async () =>
    {
        var r = await Call("GET", $"/admin/api/audit?q={Uri.EscapeDataString("/admins")}&limit=50", admin: true);
        return r.Body.Contains(supName) || r.Body.Contains("\"/admins\"");
    });
    Check("audit log has the admin creation (actor root)", seen);
    var deniedSeen = await Eventually(async () =>
    {
        var r = await Call("GET", $"/admin/api/audit?actor={supName}&limit=50", admin: true);
        return r.Body.Contains("insufficient") || r.Body.Contains("\"status\":403");
    });
    Check("audit log records the denied attempt (status 403)", deniedSeen);
    var secretCall = await Call("PUT", "/admin/api/config/smoke_secret", new { value = new { apiKey = "TOPSECRET" + run, x = 1 } }, admin: true);
    var redacted = await Eventually(async () =>
    {
        var r = await Call("GET", "/admin/api/audit?q=smoke_secret&limit=10", admin: true);
        return r.Body.Contains("smoke_secret") && !r.Body.Contains("TOPSECRET" + run);
    });
    Check("secrets in request bodies are hidden in the audit log", secretCall.Status == 200 && redacted, secretCall.Body);
    await Call("DELETE", "/admin/api/config/smoke_secret", admin: true);

    // ---- config history + rollback ----
    await Call("PUT", "/admin/api/config/smoke_hist", new { value = new { v = 1 } }, admin: true);
    await Call("PUT", "/admin/api/config/smoke_hist", new { value = new { v = 2 } }, admin: true);
    var hist = await Call("GET", "/admin/api/config/smoke_hist/history", admin: true);
    var histId = hist.Json is { } hj && hj.TryGetProperty("items", out var hitems) && hitems.GetArrayLength() > 0
        ? hitems[0].GetProperty("id").GetInt64() : 0;
    Check("config history keeps the replaced value", hist.Status == 200 && histId > 0 && hist.Body.Contains("\"v\":1"), hist.Body);
    if (histId > 0)
    {
        var roll = await Call("POST", $"/admin/api/config/smoke_hist/rollback/{histId}", admin: true);
        Check("rollback restores v=1", roll.Status == 200 && roll.Body.Contains("\"v\":1"), roll.Body);
    }
    await Call("DELETE", "/admin/api/config/smoke_hist", admin: true);

    // ---- dashboard ----
    var dash = await Call("GET", "/admin/api/dashboard", admin: true);
    Check("dashboard has live numbers and totals", dash.Status == 200 && dash.Body.Contains("\"online\"") && dash.Body.Contains("\"totals\"") && dash.Body.Contains("signupsPerDay"), dash.Body);
    var series = await Call("GET", "/admin/api/dashboard/series?minutes=60", admin: true);
    Check("dashboard series answers", series.Status == 200 && series.Body.Contains("\"items\""), series.Body);

    // ---- player storage + timeline ----
    var put = await Call("PUT", $"/admin/api/users/{a.Id}/storage/smoke_key", new { value = new { level = 7, run } }, admin: true);
    Check("admin writes a storage key", put.Status == 204, put.Body);
    var list = await Call("GET", $"/admin/api/users/{a.Id}/storage", admin: true);
    Check("admin lists the storage key", list.Status == 200 && list.Body.Contains("smoke_key") && list.Body.Contains("\"level\":7"), list.Body);
    var delKey = await Call("DELETE", $"/admin/api/users/{a.Id}/storage/smoke_key", admin: true);
    var list2 = await Call("GET", $"/admin/api/users/{a.Id}/storage", admin: true);
    Check("admin deletes the storage key", delKey.Status == 204 && !list2.Body.Contains("smoke_key"), delKey.Body);
    var missing = await Call("PUT", "/admin/api/users/no-such-user/storage/k", new { value = 1 }, admin: true);
    Check("storage write for an unknown user -> 404", missing.Status == 404, missing.Body);
    var tl = await Call("GET", $"/admin/api/users/{a.Id}/timeline?limit=50", admin: true);
    Check("player timeline shows account and wallet events", tl.Status == 200 && tl.Body.Contains("\"account\"") && tl.Body.Contains("\"ledger\""), tl.Body);

    // ---- live-ops: scheduled broadcasts ----
    var future = await Call("POST", "/admin/api/liveops/broadcasts", new { subject = "future " + run, audience = "all", runAtUtc = DateTime.UtcNow.AddDays(1) }, admin: true);
    var futureId = S(future.Json, "id") ?? "";
    Check("admin schedules a broadcast for tomorrow", future.Status == 200 && S(future.Json, "status") == "pending", future.Body);
    var cancel = await Call("DELETE", $"/admin/api/liveops/broadcasts/{futureId}", admin: true);
    Check("admin cancels it", cancel.Status == 204, cancel.Body);
    var badAudience = await Call("POST", "/admin/api/liveops/broadcasts", new { subject = "x", audience = "nobody" }, admin: true);
    Check("an invalid audience is refused", badAudience.Status >= 400, badAudience.Body);
    var badReward = await Call("POST", "/admin/api/liveops/broadcasts", new { subject = "x", reward = new { wallet = new { smokegold = -5 } } }, admin: true);
    Check("a negative reward is refused", badReward.Status >= 400, badReward.Body);

    var now = await Call("POST", "/admin/api/liveops/broadcasts", new { subject = "live now " + run, audience = "online" }, admin: true);
    var nowId = S(now.Json, "id") ?? "";
    Check("admin queues a broadcast for now", now.Status == 200, now.Body);
    var sentOk = await Eventually(async () =>
    {
        var r = await Call("GET", "/admin/api/liveops/broadcasts?limit=50", admin: true);
        if (r.Json is not { } j || !j.TryGetProperty("items", out var arr)) return false;
        foreach (var it in arr.EnumerateArray())
            if (it.GetProperty("id").ToString() == nowId && it.GetProperty("status").GetString() == "sent") return true;
        return false;
    }, 45);
    Check("the background service sends it (within ~20 s)", sentOk);
    await Call("DELETE", $"/admin/api/liveops/broadcasts/{nowId}", admin: true);

    // ---- modules + RPC tester ----
    var mods = await Call("GET", "/admin/api/modules", admin: true);
    Check("modules lists the loaded RPCs", mods.Status == 200 && mods.Body.Contains("\"hello\""), mods.Body);
    var rpc = await Call("POST", "/admin/api/rpc-test", new { userId = a.Id, id = "hello", payload = "{}" }, admin: true);
    Check("RPC tester runs 'hello' as A", rpc.Status == 200 && S(rpc.Json, "ok") == "true" && rpc.Body.Contains(a.Id), rpc.Body);
    var rpcMissing = await Call("POST", "/admin/api/rpc-test", new { userId = a.Id, id = "no_such_rpc", payload = "{}" }, admin: true);
    Check("RPC tester: unknown RPC -> 404", rpcMissing.Status == 404, rpcMissing.Body);

    // ---- cleanup ----
    var d1 = await Call("DELETE", $"/admin/api/admins/{supId}", admin: true);
    var roId = S(mkRo.Json, "account", "id") ?? "";
    var d2 = await Call("DELETE", $"/admin/api/admins/{roId}", admin: true);
    Check("admin deletes the test admins", d1.Status == 204 && d2.Status == 204, d1.Body + d2.Body);
});

await Section("rate limit", async () =>
{
    var rt = ra!;
    var limited = 0;
    for (var i = 0; i < 14; i++)
    {
        var r = await rt.Req("channel.send", new { channel = "room:smoke", text = $"spam {i} {run}" });
        if (r is not null && S(r, "data", "code") == "rate_limited") limited++;
    }
    Check("flooding triggers rate_limited", limited > 0, $"limited={limited}");
});

await Section("cleanup (deleting accounts runs account.delete + group cleanup)", async () =>
{
    var delGroup = await Call("DELETE", $"/v1/groups/{groupId}", token: b.Token);
    Check("new owner B deletes the clan", delGroup.Status == 204, delGroup.Body);
    foreach (var p in new[] { a, b, c })
    {
        var del = await Call("DELETE", "/v1/account", token: p.Token);
        Check($"delete account {p.Label}", del.Status == 204, del.Body);
    }
    await Task.Delay(300);
    Check("A's websocket closed after account deletion", ra!.Closed);
    foreach (var r in new[] { ra, rb, rc }) if (r is not null) await r.Close();
});

Log("");
Log($"RESULT: {passed} passed, {failed} failed");
Log(failed == 0 ? "ALL GOOD" : "SOME CHECKS FAILED - send smoke-test.log and server.log");
return failed == 0 ? 0 : 1;

// =============================================================================================================


sealed class MatchClient
{
    private readonly ClientWebSocket _ws;
    private MatchClient(ClientWebSocket ws) => _ws = ws;

    /// <summary>Connects with a join token; null when the server refuses (it answers 401 before the upgrade).</summary>
    public static async Task<MatchClient?> TryConnect(Uri uri)
    {
        var ws = new ClientWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(uri, cts.Token);
            return new MatchClient(ws);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task Send(byte[] data) =>
        _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);

    public async Task<byte[]?> Receive(int timeoutMs = 4000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[16 * 1024];
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            return result.MessageType == WebSocketMessageType.Close ? null : buffer[..result.Count];
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> WaitClosed(int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return true;
            }
        }
        catch (Exception)
        {
            return true; // aborted = closed
        }

        return false;
    }
}

record Resp(int Status, string Body, JsonElement? Json);
record Player(string Label, string Id, string Token, string Username);

/// <summary>A realtime client over WebSocket or TCP. Collects every frame the server sends.</summary>
sealed class Rt
{
    private readonly string _name;
    private readonly Func<string, Task> _send;
    private readonly Func<Task> _close;
    private readonly List<(JsonElement Frame, bool Used)> _frames = new();
    private readonly object _lock = new();
    private int _nextId;

    public bool Closed { get; private set; }

    private Rt(string name, Func<string, Task> send, Func<Task> close)
    {
        _name = name;
        _send = send;
        _close = close;
    }

    private void Add(string text)
    {
        try
        {
            var frame = JsonDocument.Parse(text).RootElement.Clone();
            lock (_lock) _frames.Add((frame, false));
        }
        catch (JsonException) { /* ignore */ }
    }

    public static async Task<Rt> ConnectWs(string name, Uri uri)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, CancellationToken.None);
        Rt? self = null;
        self = new Rt(name,
            text => ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None),
            async () =>
            {
                try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch (Exception) { /* already closed */ }
            });

        _ = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) { self.Closed = true; return; }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    self.Add(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            catch (Exception) { /* connection ended */ }
            finally { self.Closed = true; }
        });
        return self;
    }

    public static async Task<Rt> ConnectTcp(string name, string host, int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port);
        var stream = client.GetStream();
        var writeLock = new SemaphoreSlim(1, 1);
        Rt? self = null;
        self = new Rt(name,
            async text =>
            {
                var payload = Encoding.UTF8.GetBytes(text);
                var frame = new byte[4 + payload.Length];
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
                payload.CopyTo(frame, 4);
                await writeLock.WaitAsync();
                try { await stream.WriteAsync(frame); } finally { writeLock.Release(); }
            },
            () => { client.Close(); return Task.CompletedTask; });

        _ = Task.Run(async () =>
        {
            var header = new byte[4];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    var payload = new byte[length];
                    await stream.ReadExactlyAsync(payload);
                    self.Add(Encoding.UTF8.GetString(payload));
                }
            }
            catch (Exception) { /* connection ended */ }
            finally { self.Closed = true; }
        });
        return self;
    }

    public Task Send(string type, string id, object data) =>
        _send(JsonSerializer.Serialize(new { type, id, data }));

    /// <summary>Sends a request and waits for the frame with the same id (a reply or an error). Null on timeout.</summary>
    public async Task<JsonElement?> Req(string type, object data, int timeoutMs = 4000)
    {
        var id = $"{_name}-{Interlocked.Increment(ref _nextId)}";
        await Send(type, id, data);
        return await Wait(f => f.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == id, timeoutMs);
    }

    /// <summary>Waits for a not-yet-used frame that matches, marks it used and returns it. Null on timeout.</summary>
    public async Task<JsonElement?> Wait(Func<JsonElement, bool> match, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_lock)
            {
                for (var i = 0; i < _frames.Count; i++)
                {
                    if (!_frames[i].Used && match(_frames[i].Frame))
                    {
                        var frame = _frames[i].Frame;
                        _frames[i] = (frame, true);
                        return frame;
                    }
                }
            }

            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(25);
        }
    }

    public async Task Close() => await _close();
}

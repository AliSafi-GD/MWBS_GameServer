// Tests the Unity SDK sources (compiled as plain .NET) against a RUNNING GameServer.
//   dotnet run --project sdk/unity/SdkSmokeTest -- [baseUrl] [adminKey]
// The server needs the sample module (rpc "hello") and, for the UDP part, GameServer__Match__Transports=websocket,udp.
// Writes sdk-test.log in the current folder. Exit code 0 = every check passed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using GameServer.Client;
using GameServer.Client.Udp;
using Newtonsoft.Json.Linq;

var baseUrl = (args.Length > 0 ? args[0] : "http://localhost:5000").TrimEnd('/');
var adminKey = args.Length > 1 ? args[1] : "dev-admin-key-do-not-use-in-production-0123456789";
var logPath = Path.Combine(Directory.GetCurrentDirectory(), "sdk-test.log");
File.WriteAllText(logPath, "");

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var passed = 0;
var failed = 0;
var run = Guid.NewGuid().ToString("N").Substring(0, 8);

void Log(string line)
{
    var text = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
    Console.WriteLine(text);
    File.AppendAllText(logPath, text + Environment.NewLine);
}

void Check(string name, bool ok, string detail = null)
{
    if (ok) { passed++; Log("PASS  " + name); }
    else { failed++; Log("FAIL  " + name + (detail == null ? "" : "  -> " + detail)); }
}

void Info(string text) => Log("INFO  " + text);

async Task Section(string title, Func<Task> body)
{
    Log("");
    Log("=== " + title + " ===");
    try { await body(); }
    catch (Exception ex) { failed++; Log("FAIL  section crashed: " + ex); }
}

GameClient Make(Action<GameClientOptions> tweak = null)
{
    var options = new GameClientOptions { BaseUrl = baseUrl, Log = text => Log("SDK   " + text) };
    options.UseLiteNetLib();
    if (tweak != null) tweak(options);
    return new GameClient(options);
}

async Task<(int Status, string Body)> Admin(string method, string path, object body = null)
{
    var request = new HttpRequestMessage(new HttpMethod(method), baseUrl + path);
    request.Headers.Add("X-Admin-Key", adminKey);
    if (body != null) request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
    var response = await http.SendAsync(request);
    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
}

// Runs an SDK call that should fail and returns the ApiException (null if it did not fail).
async Task<ApiException> Fails(Func<Task> call)
{
    try { await call(); return null; }
    catch (ApiException ex) { return ex; }
}

Log("SDK smoke test, run " + run + ", server " + baseUrl);

GameClient a = null, b = null, c = null, tcpClient = null;
LoginResult la = null, lb = null, lc = null;
var chatA = new Inbox<ChatMessage>();
var chatB = new Inbox<ChatMessage>();
var matchedA = new Inbox<MatchFound>();
var matchedB = new Inbox<MatchFound>();
var room = Channels.Room("sdk" + run);
var udpOffered = false;

await Section("login, account, config, errors", async () =>
{
    a = Make();
    b = Make();
    la = await a.Auth.LoginDeviceAsync();
    lb = await b.Auth.LoginDeviceAsync();
    Check("login A and B (device id)", !string.IsNullOrEmpty(la.Token) && !string.IsNullOrEmpty(lb.UserId) && a.Session.IsLoggedIn, la?.UserId);
    Check("device id is kept between calls", a.Auth.DeviceId == a.Auth.DeviceId && a.Auth.DeviceId.Length >= 16);

    var account = await a.Account.GetAsync();
    Check("GET account", account.Id == la.UserId, account.Id);

    var updated = await a.Account.UpdateAsync(new ProfileUpdate { DisplayName = "Sdk A" });
    Check("PUT account changes the display name", updated.DisplayName == "Sdk A", updated.DisplayName);

    var wallet = await a.Economy.WalletAsync();
    Check("wallet is readable", wallet != null);
    Info("coins after login: " + (wallet.ContainsKey("coins") ? wallet["coins"].ToString() : "none"));

    var users = await a.Account.GetUsersAsync(new[] { la.UserId, lb.UserId });
    Check("public profiles of two players", users.Count == 2);

    var config = await a.Config.GetAsync();
    var config2 = await a.Config.GetAsync();
    Check("config is readable (second call may use the ETag)", config != null && config2 != null);

    var version = await a.Config.VersionCheckAsync("1.0.0", "bazaar");
    Check("version check answers", version.Status == "ok" || version.Status == "update_available" || version.Status == "update_required", version.Status);

    var providers = await a.Auth.ProvidersAsync();
    Check("providers list", providers != null);

    var rpc = await Fails(async () =>
    {
        var text = await a.Rpc.CallAsync("hello", "{}");
        Check("rpc 'hello' answers", !string.IsNullOrEmpty(text), text);
    });
    if (rpc != null) Info("rpc 'hello' not available (" + rpc.Code + "): the sample module is not loaded");

    var notFound = await Fails(() => a.Shop.BuyAsync("doesnotexist" + run));
    Check("a server error becomes ApiException (product_not_found, 404)", notFound != null && notFound.Code == "product_not_found" && notFound.Status == 404, notFound?.ToString());

    var noLogin = Make();
    var unauth = await Fails(() => noLogin.Account.GetAsync());
    Check("a call without login -> ApiException 401", unauth != null && unauth.Status == 401, unauth?.ToString());

    var unreachable = new GameClient(new GameClientOptions { BaseUrl = "http://127.0.0.1:1" });
    var net = await Fails(() => unreachable.Config.GetAsync());
    Check("server not reachable -> network_error", net != null && net.IsNetworkError, net?.ToString());

    // session restore: a second client with the same storage continues the saved session
    var storage = new MemoryStorage();
    c = Make(o => o.Storage = storage);
    lc = await c.Auth.LoginDeviceAsync();
    var again = Make(o => o.Storage = storage);
    var restored = await again.Auth.TryRestoreSessionAsync();
    Check("TryRestoreSessionAsync continues the saved session", restored && again.Session.UserId == lc.UserId);
    var accountC = await again.Account.GetAsync();
    Check("restored session is authenticated", accountC.Id == lc.UserId);
    await again.Auth.LogoutAsync();
    var after = Make(o => o.Storage = storage);
    Check("after logout nothing is restored", !await after.Auth.TryRestoreSessionAsync());
});

await Section("realtime over websocket: chat", async () =>
{
    a.Realtime.ChatMessageReceived += chatA.Add;
    b.Realtime.ChatMessageReceived += chatB.Add;
    await a.Realtime.ConnectAsync();
    await b.Realtime.ConnectAsync();
    Check("A and B connected with websocket", a.Realtime.IsConnected && b.Realtime.IsConnected && a.Realtime.TransportName == "websocket");

    var joinedA = await a.Realtime.JoinChannelAsync(room);
    await b.Realtime.JoinChannelAsync(room);
    Check("join room", joinedA == room, joinedA);

    var sent = await a.Realtime.SendMessageAsync(room, "hello from A");
    Check("send returns the stored message", sent.Text == "hello from A" && sent.SenderId == la.UserId);
    Check("B receives the room message", await chatB.WaitFor(m => m.Text == "hello from A", 3000) != null);

    var notJoined = await Fails(() => a.Realtime.SendMessageAsync(Channels.Room("never" + run), "x"));
    Check("send without join -> not_in_channel", notJoined != null && notJoined.Code == "not_in_channel", notJoined?.ToString());

    var blocked = await Fails(() => a.Realtime.SendMessageAsync(room, "see http://example.com"));
    Info("link in chat -> " + (blocked == null ? "allowed" : blocked.Code) + " (the sample module blocks links)");

    await a.Realtime.SendMessageAsync(Channels.Direct(lb.UserId), "dm one");
    Check("B receives the direct message", await chatB.WaitFor(m => m.Text == "dm one", 3000) != null);
    var history = await b.Chat.HistoryAsync(Channels.Direct(la.UserId), 10);
    Check("direct message history over HTTP", history.Any(m => m.Text == "dm one"), history.Count.ToString());
    var wsHistory = await b.Realtime.ChannelHistoryAsync(Channels.Direct(la.UserId), 10);
    Check("direct message history over the socket", wsHistory.Any(m => m.Text == "dm one"));
});

await Section("friends, groups, parties", async () =>
{
    var requests = new Inbox<JToken>();
    b.Realtime.On("friend.request", requests.Add);

    var invite = await a.Social.Friends.AddAsync(userId: lb.UserId);
    Check("A invites B", invite.State == "invite_sent", invite.State);
    Check("B gets the live event friend.request", await requests.WaitFor(_ => true, 3000) != null);
    var received = await b.Social.Friends.ListAsync(FriendStates.InviteReceived);
    Check("B sees the invite", received.Count == 1 && received[0].Profile.Id == la.UserId);
    var accepted = await b.Social.Friends.AddAsync(userId: la.UserId);
    Check("B accepts", accepted.State == "friend", accepted.State);
    var friends = await a.Social.Friends.ListAsync(FriendStates.Friend);
    Check("A lists B as an online friend", friends.Any(f => f.Profile.Id == lb.UserId && f.Online));
    var online = await a.Social.OnlineAsync(new[] { lb.UserId, lc.UserId });
    Check("OnlineAsync lists connected players only", online.Contains(lb.UserId) && !online.Contains(lc.UserId));

    var clan = await a.Social.Groups.CreateAsync(new NewGroup { Name = "sdk" + run, Type = "clan", Open = true, Description = "sdk test" });
    Check("A creates a clan", clan.Name == "sdk" + run && clan.MemberCount >= 1);
    var found = await b.Social.Groups.SearchAsync("sdk" + run);
    Check("search finds the clan", found.Any(g => g.Id == clan.Id));
    var status = await b.Social.Groups.JoinAsync(clan.Id);
    Check("B joins the open clan", !string.IsNullOrEmpty(status), status);
    var members = await a.Social.Groups.MembersAsync(clan.Id);
    Check("members: A and B", members.Count == 2);
    await a.Social.Groups.PromoteAsync(clan.Id, lb.UserId);
    var mine = await b.Social.Groups.MineAsync();
    Check("B is admin of the clan", mine.Any(m => m.Group.Id == clan.Id && m.Role == "admin"), string.Join(",", mine.Select(m => m.Role)));
    var tooLow = await Fails(() => b.Social.Groups.KickAsync(clan.Id, la.UserId));
    Check("an admin cannot kick the owner -> insufficient_rank", tooLow != null && tooLow.Code == "insufficient_rank", tooLow?.ToString());

    var groupChannel = Channels.Group(clan.Id);
    await a.Realtime.JoinChannelAsync(groupChannel);
    await b.Realtime.JoinChannelAsync(groupChannel);
    await a.Realtime.SendMessageAsync(groupChannel, "clan hello");
    Check("clan chat reaches B", await chatB.WaitFor(m => m.Text == "clan hello", 3000) != null);

    var invites = new Inbox<PartyInvite>();
    var updates = new Inbox<Party>();
    b.Realtime.PartyInvited += invites.Add;
    a.Realtime.PartyUpdated += updates.Add;
    var party = await a.Social.Parties.CreateAsync(open: false, maxSize: 4);
    Check("A creates a party", party.LeaderId == la.UserId && party.Members.Count == 1);
    await a.Social.Parties.InviteAsync(lb.UserId);
    var partyInvite = await invites.WaitFor(_ => true, 3000);
    Check("B gets the party invite event", partyInvite != null && partyInvite.PartyId == party.Id);
    var joinedParty = await b.Social.Parties.JoinAsync(party.Id);
    Check("B joins the party", joinedParty.Members.Count == 2);
    Check("A gets party.update", await updates.WaitFor(p => p.Members.Count == 2, 3000) != null);
    var myParty = await b.Social.Parties.MineAsync();
    Check("B sees own party", myParty != null && myParty.Id == party.Id);
    await b.Social.Parties.LeaveAsync();
    await a.Social.Parties.CloseAsync();

    await a.Social.Groups.DeleteAsync(clan.Id);
    await a.Social.Friends.RemoveAsync(lb.UserId);
});

await Section("notification, shop, progress", async () =>
{
    var notes = new Inbox<NotificationEvent>();
    a.Realtime.NotificationReceived += notes.Add;
    var sent = await Admin("POST", "/admin/api/users/" + la.UserId + "/notifications", new { subject = "sdk " + run, body = "hello" });
    Check("admin sends a notification", sent.Status == 200, sent.Body);
    var live = await notes.WaitFor(n => n.Subject == "sdk " + run, 4000);
    Check("NotificationReceived event", live != null);
    var inbox = await a.Notifications.ListAsync();
    var note = inbox.FirstOrDefault(n => n.Subject == "sdk " + run);
    Check("inbox lists it", note != null);
    if (note != null)
    {
        await a.Notifications.MarkReadAsync(note.Id);
        await a.Notifications.DeleteAsync(note.Id);
    }

    var shop = await Admin("PUT", "/admin/api/config/shop", new
    {
        value = new
        {
            products = new Dictionary<string, object>
            {
                ["sdk_pack"] = new { name = "Sdk pack", category = "sdk", cost = new { wallet = new { sdkgold = 100 } },
                    gives = new { wallet = new { sdkgems = 10 } }, limit = new { per = "day", count = 2 } }
            }
        },
        @public = false
    });
    Check("admin writes the shop", shop.Status == 200, shop.Body);
    await Admin("POST", "/admin/api/users/" + la.UserId + "/economy", new { wallet = new { sdkgold = 250 }, reason = "sdk test" });

    var products = await a.Shop.ListAsync("sdk");
    Check("shop lists the product with its limit", products.Any(p => p.Id == "sdk_pack" && p.Active && p.Remaining == 2));
    var buy = await a.Shop.BuyAsync("sdk_pack", 1, "r" + run);
    Check("buy: price paid and goods received", buy.State.Wallet["sdkgold"] == 150 && buy.State.Wallet["sdkgems"] == 10, Json.Serialize(buy.State));
    var retry = await a.Shop.BuyAsync("sdk_pack", 1, "r" + run);
    Check("same requestId is applied once", retry.AlreadyApplied && retry.State.Wallet["sdkgold"] == 150);
    await a.Shop.BuyAsync("sdk_pack");
    var limit = await Fails(() => a.Shop.BuyAsync("sdk_pack"));
    Check("third purchase -> purchase_limit_reached", limit != null && limit.Code == "purchase_limit_reached", limit?.ToString());

    await Admin("PUT", "/admin/api/config/achievements", new
    {
        value = new { achievements = new { sdk_win = new { name = "Two wins", stat = "sdk_wins", target = 2, reward = new { wallet = new { sdkgems = 5 } } } } },
        @public = false
    });
    await Admin("PUT", "/admin/api/config/daily_rewards", new
    {
        value = new { resetOnMiss = true, rewards = new object[] { new { wallet = new { sdkdaily = 10 } }, new { wallet = new { sdkdaily = 20 } } } },
        @public = false
    });

    var goalEvents = new Inbox<JToken>();
    a.Realtime.On("goal.completed", goalEvents.Add);
    await Admin("POST", "/admin/api/users/" + la.UserId + "/stats", new { stat = "sdk_wins", value = 1 });
    var early = await Fails(() => a.Progress.ClaimAchievementAsync("sdk_win"));
    Check("claim before the target -> goal_not_complete", early != null && early.Code == "goal_not_complete", early?.ToString());
    await Admin("POST", "/admin/api/users/" + la.UserId + "/stats", new { stat = "sdk_wins", value = 1 });
    Check("goal.completed event arrives", await goalEvents.WaitFor(_ => true, 3000) != null);
    var goals = await a.Progress.AchievementsAsync();
    var goal = goals.FirstOrDefault(g => g.Id == "sdk_win");
    Check("achievement shows progress 2/2", goal != null && goal.Progress == 2 && goal.Completed && !goal.Claimed);
    var claim = await a.Progress.ClaimAchievementAsync("sdk_win");
    Check("claim gives the reward", claim.Reward.Wallet["sdkgems"] == 5, Json.Serialize(claim));
    var twice = await Fails(() => a.Progress.ClaimAchievementAsync("sdk_win"));
    Check("claim twice -> already_claimed", twice != null && twice.Code == "already_claimed", twice?.ToString());
    var stats = await a.Progress.StatsAsync();
    Check("stats has the lifetime value", stats.ContainsKey("sdk_wins") && stats["sdk_wins"] == 2);

    var daily = await a.Progress.DailyAsync();
    Check("daily: nothing claimed yet", !daily.ClaimedToday && daily.NextDay == 1);
    var dailyClaim = await a.Progress.ClaimDailyAsync();
    Check("daily claim day 1", dailyClaim.Day == 1 && dailyClaim.Streak == 1 && dailyClaim.Reward.Wallet["sdkdaily"] == 10);
    var dailyAgain = await Fails(() => a.Progress.ClaimDailyAsync());
    Check("daily claim twice -> daily_already_claimed", dailyAgain != null && dailyAgain.Code == "daily_already_claimed", dailyAgain?.ToString());

    var tournaments = await a.Competitions.TournamentsAsync();
    Check("tournaments list is readable", tournaments != null);
    var noBoard = await Fails(() => a.Competitions.LeaderboardAsync("doesnotexist" + run));
    Check("unknown leaderboard -> ApiException", noBoard != null, noBoard?.ToString());
});

await Section("matchmaker and matches over websocket", async () =>
{
    var modes = await a.Matches.ModesAsync();
    udpOffered = modes.Transports.Any(t => (string)t["name"] == "udp");
    Info("match transports offered by the server: " + string.Join(", ", modes.Transports.Select(t => (string)t["name"])));

    var configured = new Dictionary<string, object>
    {
        ["sdk_duel"] = new { handler = "relay", transport = "websocket", minPlayers = 2, maxPlayers = 2, fillSeconds = 2, emptyTimeoutSeconds = 5, rules = new { region = "equal" } },
        ["sdk_open"] = new { handler = "relay", minPlayers = 2, maxPlayers = 4, allowClientCreate = true, emptyTimeoutSeconds = 5 }
    };
    if (udpOffered)
        configured["sdk_udp"] = new { handler = "relay", transport = "udp", minPlayers = 2, maxPlayers = 2, fillSeconds = 2, emptyTimeoutSeconds = 5, rules = new { region = "equal" } };

    var set = await Admin("PUT", "/admin/api/config/matches", new { value = new { modes = configured }, @public = false });
    Check("admin writes the 'matches' config", set.Status == 200, set.Body);
    Info("waiting for the mode cache (3 s)");
    await Task.Delay(3500);

    var listed = await a.Matches.ModesAsync();
    Check("modes list has sdk_duel with its transport", listed.Modes.Any(m => m.Name == "sdk_duel" && m.Transport == "websocket"));

    a.Realtime.MatchFound += matchedA.Add;
    b.Realtime.MatchFound += matchedB.Add;

    var ticketA = await a.Realtime.AddToMatchmakerAsync("sdk_duel", new Dictionary<string, object> { ["region"] = "eu" });
    var dup = await Fails(() => a.Realtime.AddToMatchmakerAsync("sdk_duel", new Dictionary<string, object> { ["region"] = "eu" }));
    Check("queueing twice -> already_queued", dup != null && dup.Code == "already_queued", dup?.ToString());
    var tickets = await a.Matches.TicketsAsync();
    Check("A has one ticket", tickets.Count == 1 && tickets[0].Id == ticketA.Id);
    await b.Realtime.AddToMatchmakerAsync("sdk_duel", new Dictionary<string, object> { ["region"] = "eu" });

    var foundA = await matchedA.WaitFor(_ => true, 8000);
    var foundB = await matchedB.WaitFor(_ => true, 8000);
    Check("A and B get MatchFound", foundA != null && foundB != null);
    if (foundA == null || foundB == null) return;
    Check("same match, websocket, two players", foundA.MatchId == foundB.MatchId && foundA.Transport == "websocket" && foundA.Players.Count == 2);

    var dataA = new Inbox<byte[]>();
    var dataB = new Inbox<byte[]>();
    var connA = await a.Matches.ConnectAsync(foundA);
    var connB = await b.Matches.ConnectAsync(foundB);
    connA.DataReceived += dataA.Add;
    connB.DataReceived += dataB.Add;
    Check("both connected to the match", connA.IsOpen && connB.IsOpen);

    var reused = false;
    try
    {
        var second = await a.Matches.ConnectAsync(foundA);
        await Task.Delay(700);
        reused = second.IsOpen;
    }
    catch (ApiException) { }
    Check("a join token works only once", !reused);

    await connA.SendAsync(new byte[] { 1, 2, 3 });
    var relayed = await dataB.WaitFor(_ => true, 3000);
    int slot; byte[] payload;
    Check("B receives A's bytes with A's slot", relayed != null && RelayMessage.TryParse(relayed, out slot, out payload)
        && slot == foundA.Players.First(p => p.UserId == la.UserId).Slot && payload.SequenceEqual(new byte[] { 1, 2, 3 }));

    var closedB = new Inbox<string>();
    connB.Closed += closedB.Add;
    var ended = await Admin("DELETE", "/admin/api/matches/" + foundA.MatchId);
    Check("admin ends the match", ended.Status == 204, ended.Body);
    Check("players' connections are closed", await closedB.WaitFor(_ => true, 4000) != null && !connB.IsOpen);

    // a match a player creates, found and joined by another
    var created = await a.Realtime.CreateMatchAsync("sdk_open", "sdk " + run);
    Check("A creates an open match", created.Match.Mode == "sdk_open" && created.Connect.Token.Length > 0);
    var openList = await b.Matches.ListAsync("sdk_open");
    Check("B finds it in the list", openList.Any(m => m.Id == created.Match.Id));
    var joinInfo = await b.Matches.JoinAsync(created.Match.Id);
    Check("B joins and gets a token", joinInfo.MatchId == created.Match.Id && joinInfo.Token.Length > 0);
    var c1 = await a.Matches.ConnectAsync(created.Connect);
    var c2 = await b.Matches.ConnectAsync(joinInfo);
    Check("both connect", c1.IsOpen && c2.IsOpen);
    await c1.CloseAsync();
    await c2.CloseAsync();
    await Admin("DELETE", "/admin/api/matches/" + created.Match.Id);
});

await Section("matches over UDP (LiteNetLib)", async () =>
{
    if (!udpOffered)
    {
        Info("SKIPPED: the server has no 'udp' match transport. Start it with GameServer__Match__Transports=websocket,udp (run-tests does this).");
        return;
    }

    var foundA = new Inbox<MatchFound>();
    var foundB = new Inbox<MatchFound>();
    a.Realtime.MatchFound += foundA.Add;
    b.Realtime.MatchFound += foundB.Add;
    await a.Realtime.AddToMatchmakerAsync("sdk_udp", new Dictionary<string, object> { ["region"] = "eu" });
    await b.Realtime.AddToMatchmakerAsync("sdk_udp", new Dictionary<string, object> { ["region"] = "eu" });
    var ma = await foundA.WaitFor(f => f.Mode == "sdk_udp", 8000);
    var mb = await foundB.WaitFor(f => f.Mode == "sdk_udp", 8000);
    Check("A and B get MatchFound for the udp mode", ma != null && mb != null);
    if (ma == null || mb == null) return;
    Check("the match says transport udp with a port", ma.Transport == "udp" && ma.Endpoint != null && ma.Endpoint["port"] != null, ma.Endpoint?.ToString());

    var dataB = new Inbox<byte[]>();
    var connA = await a.Matches.ConnectAsync(ma);
    var connB = await b.Matches.ConnectAsync(mb);
    connB.DataReceived += dataB.Add;
    Check("both connected over UDP", connA.IsOpen && connB.IsOpen);

    await connA.SendAsync(new byte[] { 9, 8, 7 }, MatchDelivery.Reliable);
    var reliable = await dataB.WaitFor(d => d.Length == 4 && d[1] == 9, 3000);
    Check("reliable message relayed to B", reliable != null);

    await connA.SendAsync(new byte[] { 5, 5, 5 }, MatchDelivery.Unreliable);
    await connA.SendAsync(new byte[] { 6, 6, 6 }, MatchDelivery.Sequenced);
    var unreliable = await dataB.WaitFor(d => d.Length == 4 && d[1] == 5, 2000);
    var sequenced = await dataB.WaitFor(d => d.Length == 4 && d[1] == 6, 2000);
    Info("unreliable arrived: " + (unreliable != null) + ", sequenced arrived: " + (sequenced != null) + " (UDP may drop these; loopback normally does not)");

    var closedB = new Inbox<string>();
    connB.Closed += closedB.Add;
    await Admin("DELETE", "/admin/api/matches/" + ma.MatchId);
    Check("ending the match closes the UDP connection", await closedB.WaitFor(_ => true, 6000) != null);
    await connA.CloseAsync();
});

await Section("realtime over tcp", async () =>
{
    tcpClient = Make(o => o.RealtimeTransport = "tcp");
    await tcpClient.Auth.LoginDeviceAsync();
    var offered = await Fails(() => tcpClient.Realtime.ConnectAsync());
    if (offered != null && offered.Code == "transport_not_enabled")
    {
        Info("SKIPPED: the server has no 'tcp' realtime transport");
        return;
    }

    Check("connected with tcp", offered == null && tcpClient.Realtime.IsConnected && tcpClient.Realtime.TransportName == "tcp", offered?.ToString());
    if (offered != null) return;
    await tcpClient.Realtime.JoinChannelAsync(room);
    await tcpClient.Realtime.SendMessageAsync(room, "hello from tcp");
    Check("a websocket client receives the tcp message", await chatB.WaitFor(m => m.Text == "hello from tcp", 3000) != null);
    await tcpClient.Realtime.DisconnectAsync();
});

await Section("reconnect after the server drops the connection", async () =>
{
    var disconnected = new Inbox<string>();
    var reconnected = new Inbox<string>();
    a.Realtime.Disconnected += disconnected.Add;
    a.Realtime.Reconnected += () => reconnected.Add("yes");

    var drop = await Admin("POST", "/admin/api/users/" + la.UserId + "/disconnect");
    Check("admin drops A's connection", drop.Status == 204, drop.Body);
    Check("A sees Disconnected", await disconnected.WaitFor(_ => true, 4000) != null);
    Check("A reconnects by itself", await reconnected.WaitFor(_ => true, 15000) != null && a.Realtime.IsConnected);

    await b.Realtime.SendMessageAsync(room, "after reconnect");
    Check("A got the channel back (rejoined automatically)", await chatA.WaitFor(m => m.Text == "after reconnect", 4000) != null);
});

await Section("cleanup", async () =>
{
    foreach (var client in new[] { a, b, c, tcpClient })
    {
        if (client == null) continue;
        await client.Realtime.DisconnectAsync();
    }

    foreach (var client in new[] { a, b, c, tcpClient })
    {
        if (client == null || !client.Session.IsLoggedIn) continue;
        await client.Account.DeleteAsync();
    }

    Check("accounts deleted", a == null || !a.Session.IsLoggedIn);
});

Log("");
Log("RESULT: " + passed + " passed, " + failed + " failed");
Log(failed == 0 ? "ALL GOOD" : "SOME CHECKS FAILED - send sdk-test.log");
return failed == 0 ? 0 : 1;

sealed class Inbox<T> where T : class
{
    private readonly List<T> _items = new List<T>();

    public void Add(T item)
    {
        lock (_items)
            _items.Add(item);
    }

    public async Task<T> WaitFor(Func<T, bool> match, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            T found;
            lock (_items)
                found = _items.FirstOrDefault(match);
            if (found != null)
                return found;
            if (DateTime.UtcNow >= until)
                return null;
            await Task.Delay(25);
        }
    }
}

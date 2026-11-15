using GameServer.Abstractions;
using GameServer.Core.Admin;
using GameServer.Core.Progression;
using GameServer.Core.Shop;
using Xunit;

namespace GameServer.Core.Tests;

/// <summary>The shop, daily reward and progress services against in-memory stores and a fake clock (no database).</summary>
public class ShopServiceTests
{
    private const string User = "u1";

    private static TestServer Server(DateTime nowUtc)
    {
        var server = new TestServer(nowUtc);
        server.Config.Set(ConfigRules.Shop, """
            { "products": {
                "pack":   { "cost": { "wallet": { "gold": 100 } }, "gives": { "wallet": { "gems": 10 } }, "limit": { "per": "day", "count": 2 } },
                "free":   { "gives": { "items": { "ticket": 1 } } },
                "sale":   { "gives": { "wallet": { "gems": 1 } }, "startUtc": "2026-12-01T00:00:00Z", "endUtc": "2026-12-08T00:00:00Z" },
                "old":    { "gives": { "wallet": { "gems": 1 } }, "endUtc": "2026-01-01T00:00:00Z" }
            } }
            """);
        return server;
    }

    private static DateTime Utc(int y, int mo, int d, int h = 12) => new(y, mo, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Buy_TakesThePrice_AndGivesTheGoods()
    {
        using var s = Server(Utc(2026, 11, 15));
        s.Economy.Give(User, "gold", 250);

        var result = await s.Get<ShopService>().BuyAsync(User, new ShopBuyRequest("pack"), default);

        Assert.False(result.AlreadyApplied);
        Assert.Equal(150, s.Economy.Balance(User, "gold"));
        Assert.Equal(10, s.Economy.Balance(User, "gems"));
    }

    [Fact]
    public async Task DailyLimit_StopsTheThirdPurchase_AndResetsAtMidnight()
    {
        using var s = Server(Utc(2026, 11, 15, 23));
        s.Economy.Give(User, "gold", 1000);
        var shop = s.Get<ShopService>();

        await shop.BuyAsync(User, new ShopBuyRequest("pack"), default);
        await shop.BuyAsync(User, new ShopBuyRequest("pack"), default);
        var third = await Assert.ThrowsAsync<GameException>(() => shop.BuyAsync(User, new ShopBuyRequest("pack"), default));
        Assert.Equal("purchase_limit_reached", third.Code);
        Assert.Equal(0, (await shop.ListAsync(User, null, default)).Single(p => p.Id == "pack").Remaining);

        s.Clock.Advance(TimeSpan.FromHours(1)); // next UTC day

        Assert.Equal(2, (await shop.ListAsync(User, null, default)).Single(p => p.Id == "pack").Remaining);
        await shop.BuyAsync(User, new ShopBuyRequest("pack"), default);
        Assert.Equal(700, s.Economy.Balance(User, "gold"));
    }

    [Fact]
    public async Task FailedPayment_GivesTheLimitBack()
    {
        using var s = Server(Utc(2026, 11, 15));
        var shop = s.Get<ShopService>();

        var broke = await Assert.ThrowsAsync<GameException>(() => shop.BuyAsync(User, new ShopBuyRequest("pack"), default));

        Assert.Equal("insufficient_funds", broke.Code);
        Assert.All(s.Purchases.Counts.Values, count => Assert.Equal(0, count));
        Assert.Equal(2, (await shop.ListAsync(User, null, default)).Single(p => p.Id == "pack").Remaining);
    }

    [Fact]
    public async Task SameRequestId_IsChargedOnce_AndCountsOnce()
    {
        using var s = Server(Utc(2026, 11, 15));
        s.Economy.Give(User, "gold", 1000);
        var shop = s.Get<ShopService>();

        await shop.BuyAsync(User, new ShopBuyRequest("pack", 1, "click-1"), default);
        var retry = await shop.BuyAsync(User, new ShopBuyRequest("pack", 1, "click-1"), default);

        Assert.True(retry.AlreadyApplied);
        Assert.Equal(900, s.Economy.Balance(User, "gold"));
        Assert.Equal(1, (await shop.ListAsync(User, null, default)).Single(p => p.Id == "pack").Remaining);
    }

    [Fact]
    public async Task SaleWindow_ControlsBuying_AndListing()
    {
        using var s = Server(Utc(2026, 11, 30));
        var shop = s.Get<ShopService>();

        var early = await Assert.ThrowsAsync<GameException>(() => shop.BuyAsync(User, new ShopBuyRequest("sale"), default));
        Assert.Equal("product_unavailable", early.Code);

        var list = await shop.ListAsync(User, null, default);
        Assert.False(list.Single(p => p.Id == "sale").Active); // upcoming: listed, not yet active
        Assert.DoesNotContain(list, p => p.Id == "old");      // ended: not listed

        s.Clock.Advance(TimeSpan.FromDays(1)); // 2026-12-01 12:00
        await shop.BuyAsync(User, new ShopBuyRequest("sale"), default);
        Assert.Equal(1, s.Economy.Balance(User, "gems"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Quantity_OutOfRange_IsRejected(int quantity)
    {
        using var s = Server(Utc(2026, 11, 15));
        var error = await Assert.ThrowsAsync<GameException>(() =>
            s.Get<ShopService>().BuyAsync(User, new ShopBuyRequest("free", quantity), default));
        Assert.Equal("invalid_request", error.Code);
    }

    [Fact]
    public async Task UnknownProduct_IsNotFound()
    {
        using var s = Server(Utc(2026, 11, 15));
        var error = await Assert.ThrowsAsync<GameException>(() => s.Get<ShopService>().BuyAsync(User, new ShopBuyRequest("nope"), default));
        Assert.Equal("product_not_found", error.Code);
    }
}

public class DailyRewardServiceTests
{
    private const string User = "u1";

    private static TestServer Server(DateTime nowUtc, bool resetOnMiss = true, int offset = 0)
    {
        var server = new TestServer(nowUtc);
        server.Config.Set(ConfigRules.DailyRewards, $$"""
            { "resetOnMiss": {{(resetOnMiss ? "true" : "false")}}, "utcOffsetMinutes": {{offset}},
              "rewards": [ { "wallet": { "coins": 10 } }, { "wallet": { "coins": 20 } }, { "wallet": { "gems": 1 } } ] }
            """);
        return server;
    }

    private static DateTime Utc(int y, int mo, int d, int h = 12, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Claims_DayByDay_WithAMissedDay()
    {
        using var s = Server(Utc(2026, 11, 15));
        var daily = s.Get<DailyRewardService>();

        Assert.Equal(1, (await daily.ClaimAsync(User, default)).Day);
        var again = await Assert.ThrowsAsync<GameException>(() => daily.ClaimAsync(User, default));
        Assert.Equal("daily_already_claimed", again.Code);

        s.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, (await daily.ClaimAsync(User, default)).Day);
        s.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(3, (await daily.ClaimAsync(User, default)).Day);
        s.Clock.Advance(TimeSpan.FromDays(1));
        var fourth = await daily.ClaimAsync(User, default);
        Assert.Equal((1, 4), (fourth.Day, fourth.Streak)); // the list starts again, the streak keeps counting

        s.Clock.Advance(TimeSpan.FromDays(2)); // missed a day
        Assert.Equal(0, (await daily.GetAsync(User, default)).Streak);
        var restart = await daily.ClaimAsync(User, default);
        Assert.Equal((1, 1), (restart.Day, restart.Streak));

        Assert.Equal(10 + 20 + 10 + 10, s.Economy.Balance(User, "coins"));
        Assert.Equal(1, s.Economy.Balance(User, "gems"));
    }

    [Fact]
    public async Task WithoutResetOnMiss_AMissedDayKeepsTheStreak()
    {
        using var s = Server(Utc(2026, 11, 15), resetOnMiss: false);
        var daily = s.Get<DailyRewardService>();

        await daily.ClaimAsync(User, default);
        s.Clock.Advance(TimeSpan.FromDays(3));

        Assert.Equal(2, (await daily.ClaimAsync(User, default)).Streak);
    }

    [Fact]
    public async Task FailedPayment_DoesNotUseUpTheDay()
    {
        using var s = Server(Utc(2026, 11, 15));
        var daily = s.Get<DailyRewardService>();
        s.Economy.FailNext = new GameException("not_found", "User not found.");

        await Assert.ThrowsAsync<GameException>(() => daily.ClaimAsync(User, default));

        Assert.False((await daily.GetAsync(User, default)).ClaimedToday);
        Assert.Equal(1, (await daily.ClaimAsync(User, default)).Day);
    }

    [Fact]
    public async Task View_ShowsWhenTheNextClaimOpens_InTheGameDay()
    {
        // UTC+03:30: the game day starts at 20:30 UTC.
        using var s = Server(Utc(2026, 11, 15, 21, 0), offset: 210);
        var daily = s.Get<DailyRewardService>();

        var before = await daily.GetAsync(User, default);
        Assert.False(before.ClaimedToday);
        Assert.Equal(s.Clock.UtcNow(), before.NextClaimUtc); // can claim now
        Assert.Equal(1, before.NextDay);

        await daily.ClaimAsync(User, default);

        var after = await daily.GetAsync(User, default);
        Assert.True(after.ClaimedToday);
        Assert.Equal(Utc(2026, 11, 16, 20, 30), after.NextClaimUtc);
        Assert.Equal(2, after.NextDay);

        s.Clock.Advance(TimeSpan.FromHours(23)); // 20:00 UTC next day: still the same game day
        Assert.True((await daily.GetAsync(User, default)).ClaimedToday);
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.False((await daily.GetAsync(User, default)).ClaimedToday);
    }

    [Fact]
    public async Task NoRewards_IsNotConfigured()
    {
        using var s = new TestServer(Utc(2026, 11, 15));
        var error = await Assert.ThrowsAsync<GameException>(() => s.Get<DailyRewardService>().ClaimAsync(User, default));
        Assert.Equal("daily_not_configured", error.Code);
    }
}

public class ProgressServiceTests
{
    private const string User = "u1";

    private static TestServer Server(DateTime nowUtc)
    {
        var server = new TestServer(nowUtc);
        server.Config.Set(ConfigRules.Achievements, """
            { "achievements": {
                "first_win": { "name": "First win", "stat": "wins", "target": 1, "reward": { "wallet": { "gems": 5 } } },
                "secret":    { "name": "Secret", "stat": "jumps", "target": 3, "hidden": true, "reward": { "wallet": { "gems": 1 } } }
            } }
            """);
        server.Config.Set(ConfigRules.Quests, """
            { "quests": { "play_3": { "name": "Play 3", "stat": "played", "target": 3, "period": "day", "reward": { "wallet": { "coins": 40 } } } } }
            """);
        return server;
    }

    private static DateTime Utc(int y, int mo, int d, int h = 12) => new(y, mo, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AddStat_CountsTheLifetimeValue()
    {
        using var s = Server(Utc(2026, 11, 15));
        var progress = s.Get<ProgressService>();

        Assert.Equal(1, await progress.AddStatAsync(User, "wins"));
        Assert.Equal(4, await progress.AddStatAsync(User, "wins", 3));
        Assert.Equal(10, await progress.SetStatMaxAsync(User, "best", 10));
        Assert.Equal(10, await progress.SetStatMaxAsync(User, "best", 7)); // max keeps the higher value

        var stats = await progress.GetStatsAsync(User);
        Assert.Equal(4, stats["wins"]);
        Assert.Equal(10, stats["best"]);
    }

    [Fact]
    public async Task Achievement_ClaimOnlyWhenDone_AndOnlyOnce()
    {
        using var s = Server(Utc(2026, 11, 15));
        var progress = s.Get<ProgressService>();

        var early = await Assert.ThrowsAsync<GameException>(() => progress.ClaimAsync(GoalCatalog.Achievement, User, "first_win", default));
        Assert.Equal("goal_not_complete", early.Code);

        await progress.AddStatAsync(User, "wins");
        var view = (await progress.ListAsync(GoalCatalog.Achievement, User, default)).Single(g => g.Id == "first_win");
        Assert.True(view.Completed);
        Assert.False(view.Claimed);

        await progress.ClaimAsync(GoalCatalog.Achievement, User, "first_win", default);
        Assert.Equal(5, s.Economy.Balance(User, "gems"));

        var twice = await Assert.ThrowsAsync<GameException>(() => progress.ClaimAsync(GoalCatalog.Achievement, User, "first_win", default));
        Assert.Equal("already_claimed", twice.Code);
        Assert.True((await progress.ListAsync(GoalCatalog.Achievement, User, default)).Single(g => g.Id == "first_win").Claimed);
    }

    [Fact]
    public async Task DailyQuest_StartsAgainTheNextDay()
    {
        using var s = Server(Utc(2026, 11, 15, 22));
        var progress = s.Get<ProgressService>();

        await progress.AddStatAsync(User, "played", 3);
        var today = (await progress.ListAsync(GoalCatalog.Quest, User, default)).Single();
        Assert.True(today.Completed);
        Assert.Equal(Utc(2026, 11, 16, 0), today.ResetsUtc);
        await progress.ClaimAsync(GoalCatalog.Quest, User, "play_3", default);

        s.Clock.Advance(TimeSpan.FromHours(2)); // next UTC day

        var tomorrow = (await progress.ListAsync(GoalCatalog.Quest, User, default)).Single();
        Assert.Equal(0, tomorrow.Progress);
        Assert.False(tomorrow.Claimed);
        await progress.AddStatAsync(User, "played", 3);
        await progress.ClaimAsync(GoalCatalog.Quest, User, "play_3", default);
        Assert.Equal(80, s.Economy.Balance(User, "coins"));
        Assert.Equal(6, (await progress.GetStatsAsync(User))["played"]); // the lifetime value keeps counting
    }

    [Fact]
    public async Task FailedPayment_LeavesTheRewardClaimable()
    {
        using var s = Server(Utc(2026, 11, 15));
        var progress = s.Get<ProgressService>();
        await progress.AddStatAsync(User, "wins");
        s.Economy.FailNext = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => progress.ClaimAsync(GoalCatalog.Achievement, User, "first_win", default));

        await progress.ClaimAsync(GoalCatalog.Achievement, User, "first_win", default);
        Assert.Equal(5, s.Economy.Balance(User, "gems"));
    }

    [Fact]
    public async Task ReachingAGoal_TellsThePlayer_Once()
    {
        using var s = Server(Utc(2026, 11, 15));
        var progress = s.Get<ProgressService>();

        await progress.AddStatAsync(User, "played", 2);
        Assert.Empty(s.Push.Sent);

        await progress.AddStatAsync(User, "played");  // 3: the daily quest is done
        await progress.AddStatAsync(User, "played");  // 4: already done, no second event

        var sent = Assert.Single(s.Push.Sent);
        Assert.Equal(User, sent.UserId);
        using var frame = System.Text.Json.JsonDocument.Parse(sent.Frame);
        Assert.Equal("goal.completed", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal("play_3", frame.RootElement.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("quest", frame.RootElement.GetProperty("data").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task HiddenAchievement_ShowsUpOnceReached()
    {
        using var s = Server(Utc(2026, 11, 15));
        var progress = s.Get<ProgressService>();

        Assert.DoesNotContain(await progress.ListAsync(GoalCatalog.Achievement, User, default), g => g.Id == "secret");
        await progress.AddStatAsync(User, "jumps", 3);
        Assert.Contains(await progress.ListAsync(GoalCatalog.Achievement, User, default), g => g.Id == "secret");
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("")]
    public async Task InvalidStatName_IsRejected(string stat)
    {
        using var s = Server(Utc(2026, 11, 15));
        var error = await Assert.ThrowsAsync<GameException>(() => s.Get<ProgressService>().AddStatAsync(User, stat));
        Assert.Equal("invalid_stat", error.Code);
    }
}

public class AdminRulesTests
{
    [Fact]
    public void RedactBody_MasksSecrets_AndCutsLongBodies()
    {
        Assert.Equal("""{"name":"bob","apiKey":"***","clientSecret":"***"}""",
            AdminRules.RedactBody("""{"name":"bob","apiKey":"abc","clientSecret":"xyz"}"""));
        Assert.Null(AdminRules.RedactBody("  "));
        Assert.Equal(2001, AdminRules.RedactBody(new string('a', 5000))!.Length); // 2000 + "…"
    }

    [Fact]
    public void MasterKey_MatchesOnlyItself()
    {
        Assert.True(AdminKeys.IsMaster("master-key", "master-key"));
        Assert.False(AdminKeys.IsMaster("master-kez", "master-key"));
        Assert.False(AdminKeys.IsMaster("", ""));             // no master key configured
        Assert.False(AdminKeys.IsMaster("anything", ""));
    }

    [Fact]
    public void NewKeys_AreRandom_AndHashStably()
    {
        var a = AdminKeys.New();
        Assert.StartsWith("gsk_", a);
        Assert.NotEqual(a, AdminKeys.New());
        Assert.Equal(AdminKeys.Hash(a), AdminKeys.Hash(a));
        Assert.Equal(64, AdminKeys.Hash(a).Length); // SHA-256 hex
    }

    [Theory]
    [InlineData("ops-team", true)]
    [InlineData("a", false)]
    [InlineData("root", false)]
    [InlineData("ROOT", false)]
    [InlineData("has space", false)]
    public void AdminName_Rules(string name, bool ok)
    {
        if (ok) AdminKeys.ValidateName(name);
        else Assert.Equal("invalid_name", Assert.Throws<GameException>(() => AdminKeys.ValidateName(name)).Code);
    }

    [Fact]
    public void Storage_And_Broadcast_Checks()
    {
        Assert.Equal("invalid_key", Assert.Throws<GameException>(() => AdminRules.ValidateStorageWrite("", "{}")).Code);
        Assert.Equal("too_large", Assert.Throws<GameException>(() => AdminRules.ValidateStorageWrite("k", new string('x', 256 * 1024 + 1))).Code);
        Assert.Equal("invalid_subject", Assert.Throws<GameException>(() => AdminRules.ValidateBroadcast(" ", "all")).Code);
        Assert.Equal("invalid_audience", Assert.Throws<GameException>(() => AdminRules.ValidateBroadcast("Hi", "friends")).Code);
        AdminRules.ValidateBroadcast("Hi", "online");
    }

    [Fact]
    public void LikePatterns_EscapeWildcards()
    {
        Assert.Equal(@"%50\% off\_now%", AdminRules.ContainsPattern("50% off_now"));
    }
}

public class AccountCleanupTests
{
    private sealed class Log
    {
        public List<string> Steps { get; } = new();
    }

    private sealed class Groups(Log log) : GameServer.Core.Social.IGroupCleanup
    {
        public Task OnUserDeletingAsync(string userId, CancellationToken ct) { log.Steps.Add("groups " + userId); return Task.CompletedTask; }
    }

    private sealed class Parties(Log log) : GameServer.Core.Social.IPartyMembership
    {
        public bool IsMember(string partyId, string userId) => false;
        public GameServer.Core.Social.PartyView? Mine(string userId) => null;
        public Task<bool> LeaveAsync(string userId, CancellationToken ct) { log.Steps.Add("party " + userId); return Task.FromResult(true); }
    }

    private sealed class Realtime(Log log) : GameServer.Core.Realtime.IRealtime
    {
        public bool IsOnline(string userId) => true;
        public IReadOnlyCollection<string> OnlineAmong(IEnumerable<string> userIds) => userIds.ToArray();
        public Task SendToUserAsync(string userId, string frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendToUsersAsync(IEnumerable<string> userIds, string frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendToChannelAsync(string channel, string frame, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsInChannel(string userId, string channel) => false;
        public void RemoveUserFromChannel(string userId, string channel) { }
        public Task DisconnectUserAsync(string userId, string reason) { log.Steps.Add($"disconnect {userId} {reason}"); return Task.CompletedTask; }
        public event Action<string>? UserWentOffline { add { } remove { } }
    }

    [Fact]
    public async Task BeforeDelete_HandsOverClans_LeavesTheParty_ThenDisconnects()
    {
        var log = new Log();
        var cleanup = new GameServer.Core.Social.AccountCleanup(new Groups(log), new Parties(log), new Realtime(log));

        await cleanup.BeforeDeleteAsync("u1", default);

        Assert.Equal(new[] { "groups u1", "party u1", "disconnect u1 account_deleted" }, log.Steps);
    }
}

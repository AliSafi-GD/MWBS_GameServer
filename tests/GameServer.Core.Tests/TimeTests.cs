using GameServer.Core.Progression;
using GameServer.Core.Shop;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GameServer.Core.Tests;

/// <summary>
/// Everything that depends on "now": shop sale windows and limit periods, quest/stat periods, the daily reward streak,
/// and the clock wiring in AddGameServerCore. Time is a FakeTimeProvider, so a day passes in one line.
/// </summary>
public class TimeTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static FakeTimeProvider Clock(DateTime startUtc) => new(new DateTimeOffset(startUtc));

    // ---- wiring ----------------------------------------------------------------------------------------------

    private static ServiceProvider Build(Action<IServiceCollection>? before = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x",
            ["GameServer:Match:Transports"] = "",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JwtOptions>(o => o.Secret = "unit-test-secret-unit-test-secret-0123456789"); // the host binds it
        before?.Invoke(services);
        services.AddGameServerCore(config, realtime: rt => rt.Add("websocket", _ => throw new NotSupportedException()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_Clock_IsTheSystemClock()
    {
        using var sp = Build();
        Assert.Same(TimeProvider.System, sp.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void A_Clock_RegisteredFirst_IsUsedByTheServices()
    {
        var clock = Clock(Utc(2030, 1, 2, 3, 4));
        using var sp = Build(s => s.AddSingleton<TimeProvider>(clock));

        Assert.Same(clock, sp.GetRequiredService<TimeProvider>());

        var (_, expires) = sp.GetRequiredService<TokenService>().Issue("user-1");
        Assert.Equal(Utc(2030, 1, 2, 4, 4), expires); // 60 minutes after the fake "now"

        // The services that read the clock all resolve with it (no database is opened by constructing them).
        Assert.NotNull(sp.GetRequiredService<ShopService>());
        Assert.NotNull(sp.GetRequiredService<DailyRewardService>());
        Assert.NotNull(sp.GetRequiredService<ProgressService>());
    }

    [Fact]
    public void UtcNow_HasUtcKind()
    {
        var now = Clock(Utc(2026, 11, 15, 12)).UtcNow();
        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.Equal(Utc(2026, 11, 15, 12), now);
    }

    // ---- period keys (shop limits, stats, quests) -------------------------------------------------------------

    [Theory]
    [InlineData("day", "2026-11-15")]
    [InlineData("week", "2026-W46")]
    [InlineData("month", "2026-11")]
    [InlineData("lifetime", "all")]
    public void PeriodKey_ForEachPeriod(string per, string expected)
    {
        Assert.Equal(expected, PeriodKeys.Key(per, Utc(2026, 11, 15, 23, 59)));
    }

    [Fact]
    public void PeriodKey_Week_UsesTheIsoYearAtNewYear()
    {
        // 2027-01-01 is a Friday: it still belongs to the last ISO week of 2026.
        Assert.Equal("2026-W53", PeriodKeys.Key("week", Utc(2027, 1, 1)));
        Assert.Equal("2027-W01", PeriodKeys.Key("week", Utc(2027, 1, 4))); // the Monday after
    }

    [Fact]
    public void Day_Key_Changes_AtUtcMidnight()
    {
        var clock = Clock(Utc(2026, 11, 15, 23, 59));
        var before = PeriodKeys.Key("day", clock.UtcNow());

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.NotEqual(before, PeriodKeys.Key("day", clock.UtcNow()));
        Assert.Equal("2026-11-16", PeriodKeys.Key("day", clock.UtcNow()));
    }

    [Fact]
    public void Week_Key_Stays_FromMondayToSunday()
    {
        var clock = Clock(Utc(2026, 11, 16)); // Monday
        var monday = PeriodKeys.Key("week", clock.UtcNow());

        clock.Advance(TimeSpan.FromDays(6) + TimeSpan.FromHours(23)); // Sunday 23:00
        Assert.Equal(monday, PeriodKeys.Key("week", clock.UtcNow()));

        clock.Advance(TimeSpan.FromHours(1)); // next Monday 00:00
        Assert.NotEqual(monday, PeriodKeys.Key("week", clock.UtcNow()));
    }

    [Theory]
    [InlineData("day", 2026, 11, 16)]
    [InlineData("week", 2026, 11, 16)]  // the Monday after Sunday the 15th
    [InlineData("month", 2026, 12, 1)]
    public void NextReset_IsTheStartOfTheNextPeriod(string per, int y, int m, int d)
    {
        Assert.Equal(Utc(y, m, d), PeriodKeys.NextReset(per, Utc(2026, 11, 15, 18)));
    }

    [Fact]
    public void NextReset_OnAMonday_IsTheNextMonday_AndLifetimeHasNone()
    {
        Assert.Equal(Utc(2026, 11, 23), PeriodKeys.NextReset("week", Utc(2026, 11, 16, 0, 1)));
        Assert.Null(PeriodKeys.NextReset("lifetime", Utc(2026, 11, 16)));
    }

    // ---- shop sale window -------------------------------------------------------------------------------------

    private static ShopProduct Product(DateTime? start, DateTime? end, bool enabled = true) =>
        new("p", "P", null, null, 0, enabled, Bundle.Empty, Bundle.Empty, null, start, end);

    [Fact]
    public void Shop_Product_IsOnSale_OnlyInsideItsWindow()
    {
        var product = Product(Utc(2026, 12, 1), Utc(2026, 12, 8));
        var clock = Clock(Utc(2026, 11, 30, 23, 59));

        Assert.False(product.IsActive(clock.UtcNow()));        // not yet

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(product.IsActive(clock.UtcNow()));         // start is included

        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 12, 7, 23, 59)));
        Assert.True(product.IsActive(clock.UtcNow()));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(product.IsActive(clock.UtcNow()));        // end is excluded
    }

    [Fact]
    public void Shop_Product_WithoutWindow_IsAlwaysOnSale_UnlessDisabled()
    {
        var now = Utc(2026, 11, 15);
        Assert.True(Product(null, null).IsActive(now));
        Assert.False(Product(null, null, enabled: false).IsActive(now));
    }

    // ---- daily reward -----------------------------------------------------------------------------------------

    [Fact]
    public void Daily_Today_FollowsTheGameOffset()
    {
        var now = Utc(2026, 11, 15, 21, 0);

        Assert.Equal(new DateOnly(2026, 11, 15), DailyStreak.Today(now, 0));
        Assert.Equal(new DateOnly(2026, 11, 16), DailyStreak.Today(now, 210));  // UTC+03:30: already 00:30 next day
        Assert.Equal(new DateOnly(2026, 11, 15), DailyStreak.Today(now, -300)); // UTC-05:00: 16:00 same day
    }

    [Fact]
    public void Daily_NextDayStart_IsLocalMidnight_InUtc()
    {
        Assert.Equal(Utc(2026, 11, 16), DailyStreak.NextDayStartUtc(new DateOnly(2026, 11, 15), 0));
        Assert.Equal(Utc(2026, 11, 15, 20, 30), DailyStreak.NextDayStartUtc(new DateOnly(2026, 11, 15), 210));
        Assert.Equal(DateTimeKind.Utc, DailyStreak.NextDayStartUtc(new DateOnly(2026, 11, 15), 210).Kind);
    }

    [Theory]
    [InlineData(5, 0, true, 5)]    // claimed today
    [InlineData(5, -1, true, 5)]   // claimed yesterday: the streak is still alive
    [InlineData(5, -2, true, 0)]   // missed a day and the game resets
    [InlineData(5, -2, false, 5)]  // missed a day, the game keeps the streak
    public void Daily_CurrentStreak(int streak, int lastOffsetDays, bool resetOnMiss, int expected)
    {
        var today = new DateOnly(2026, 11, 15);
        Assert.Equal(expected, DailyStreak.Current(streak, today.AddDays(lastOffsetDays), today, resetOnMiss));
    }

    [Fact]
    public void Daily_CurrentStreak_NeverClaimed_IsZero()
    {
        Assert.Equal(0, DailyStreak.Current(0, null, new DateOnly(2026, 11, 15), resetOnMiss: true));
    }

    [Theory]
    [InlineData(0, null, true, 1)]   // first claim ever
    [InlineData(3, -1, true, 4)]     // claimed yesterday
    [InlineData(3, -2, true, 1)]     // missed: back to day 1
    [InlineData(3, -2, false, 4)]    // missed, but the game keeps counting
    public void Daily_StreakAfterClaim(int streak, int? lastOffsetDays, bool resetOnMiss, int expected)
    {
        var today = new DateOnly(2026, 11, 15);
        DateOnly? last = lastOffsetDays is { } d ? today.AddDays(d) : null;
        Assert.Equal(expected, DailyStreak.AfterClaim(streak, last, today, resetOnMiss));
    }

    [Theory]
    [InlineData(1, 7, 1)]
    [InlineData(7, 7, 7)]
    [InlineData(8, 7, 1)]   // after the last reward the list starts again
    [InlineData(15, 7, 1)]
    [InlineData(5, 1, 1)]
    public void Daily_RewardDay_WrapsAround(int streak, int count, int expected)
    {
        Assert.Equal(expected, DailyStreak.RewardDay(streak, count));
    }

    /// <summary>Plays the claim logic of DailyRewardService day by day against a fake clock.</summary>
    [Fact]
    public void Daily_AWeekOfClaims_WithAMissedDay()
    {
        const int offset = 210; // the game's day starts at 20:30 UTC
        const int rewards = 3;
        var clock = Clock(Utc(2026, 11, 15, 21, 0));
        var streak = 0;
        DateOnly? last = null;
        var days = new List<int>();

        void Claim()
        {
            var today = DailyStreak.Today(clock.UtcNow(), offset);
            Assert.NotEqual(last, today); // the service answers daily_already_claimed here
            streak = DailyStreak.AfterClaim(streak, last, today, resetOnMiss: true);
            last = today;
            days.Add(DailyStreak.RewardDay(streak, rewards));
        }

        Claim();                                    // day 1
        clock.Advance(TimeSpan.FromHours(3));       // 00:00 UTC: still the same game day
        Assert.Equal(last, DailyStreak.Today(clock.UtcNow(), offset));
        clock.Advance(TimeSpan.FromHours(21));      // next game day
        Claim();                                    // day 2
        clock.Advance(TimeSpan.FromDays(1));
        Claim();                                    // day 3
        clock.Advance(TimeSpan.FromDays(1));
        Claim();                                    // wraps to reward 1, streak 4
        Assert.Equal(4, streak);

        clock.Advance(TimeSpan.FromDays(2));        // skipped a day
        var today = DailyStreak.Today(clock.UtcNow(), offset);
        Assert.Equal(0, DailyStreak.Current(streak, last, today, resetOnMiss: true));
        Claim();                                    // starts again
        Assert.Equal(1, streak);

        Assert.Equal(new[] { 1, 2, 3, 1, 1 }, days);
    }
}

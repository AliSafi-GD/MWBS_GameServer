using GameServer.Abstractions;

namespace GameServer.Core.Progression;

public sealed record DailyClaimRequest;

public sealed record DailyView(
    int Streak, bool ClaimedToday, int NextDay, IReadOnlyList<Bundle> Rewards, DateTime NextClaimUtc, int UtcOffsetMinutes);

public sealed record DailyClaimResult(int Day, int Streak, Bundle Reward, EconomyState State);

/// <summary>The date and streak rules of the daily reward, without the database (so they can be tested).</summary>
public static class DailyStreak
{
    /// <summary>The game's calendar day at <paramref name="nowUtc"/> ("daily_rewards".utcOffsetMinutes moves midnight).</summary>
    public static DateOnly Today(DateTime nowUtc, int utcOffsetMinutes) =>
        DateOnly.FromDateTime(nowUtc.AddMinutes(utcOffsetMinutes));

    /// <summary>When the day after <paramref name="today"/> starts, in UTC.</summary>
    public static DateTime NextDayStartUtc(DateOnly today, int utcOffsetMinutes) =>
        today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(-utcOffsetMinutes);

    /// <summary>The streak as the player sees it now: a missed day shows 0 when the game resets on a miss.</summary>
    public static int Current(int streak, DateOnly? last, DateOnly today, bool resetOnMiss) =>
        last == today || last == today.AddDays(-1) || !resetOnMiss ? streak : 0;

    /// <summary>The streak after claiming today (the caller has already checked that today is not claimed).</summary>
    public static int AfterClaim(int streak, DateOnly? last, DateOnly today, bool resetOnMiss) =>
        last is null ? 1
        : last == today.AddDays(-1) ? streak + 1
        : resetOnMiss ? 1
        : streak + 1;

    /// <summary>Which reward (1-based) a streak gets; the list starts again after the last day.</summary>
    public static int RewardDay(int streak, int rewardCount) => (streak - 1) % rewardCount + 1;
}

/// <summary>Daily login reward with a streak (config key "daily_rewards").</summary>
public sealed class DailyRewardService
{
    private readonly IDailyRewardStore _store;
    private readonly IEconomy _economy;
    private readonly IConfigStore _config;
    private readonly HookRunner _hooks;
    private readonly TimeProvider _time;

    public DailyRewardService(IDailyRewardStore store, IEconomy economy, IConfigStore config, HookRunner hooks, TimeProvider time)
    {
        _store = store;
        _economy = economy;
        _config = config;
        _hooks = hooks;
        _time = time;
    }

    public async Task<DailyCatalog> CatalogAsync(CancellationToken ct) =>
        DailyCatalog.From(await _config.GetAsync(ConfigRules.DailyRewards, ct));

    public async Task<DailyView> GetAsync(string userId, CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        var now = _time.UtcNow();
        var today = DailyStreak.Today(now, catalog.UtcOffsetMinutes);

        var (streak, last) = await _store.GetAsync(userId, ct);

        var claimedToday = last == today;
        var effective = DailyStreak.Current(streak, last, today, catalog.ResetOnMiss);
        var nextDay = DailyStreak.RewardDay(effective + 1, Math.Max(catalog.Rewards.Count, 1));
        var nextClaim = DailyStreak.NextDayStartUtc(today, catalog.UtcOffsetMinutes);

        return new DailyView(effective, claimedToday, nextDay, catalog.Rewards, claimedToday ? nextClaim : now, catalog.UtcOffsetMinutes);
    }

    public Task<DailyClaimResult> ClaimAsync(string userId, CancellationToken ct) =>
        _hooks.RunAsync(HookOps.DailyClaim, userId, new DailyClaimRequest(), _ => DoClaimAsync(userId, ct), ct);

    private async Task<DailyClaimResult> DoClaimAsync(string userId, CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        if (catalog.Rewards.Count == 0)
            throw new GameException("daily_not_configured", "This game has no daily reward.");

        var today = DailyStreak.Today(_time.UtcNow(), catalog.UtcOffsetMinutes);

        await using var claim = await _store.BeginClaimAsync(userId, ct);
        var (streak, last) = claim.State;

        if (last == today)
            throw new GameException("daily_already_claimed", "Today's reward was already taken.");

        var newStreak = DailyStreak.AfterClaim(streak, last, today, catalog.ResetOnMiss);
        var day = DailyStreak.RewardDay(newStreak, catalog.Rewards.Count);
        var reward = catalog.Rewards[day - 1];

        await claim.SaveAsync(newStreak, today, ct);

        var transaction = new EconomyTransaction("daily_reward", $"daily:{today:yyyy-MM-dd}");
        reward.AddTo(transaction, +1);
        var result = await _economy.ApplyAsync(userId, transaction, ct);

        await claim.CommitAsync(ct); // if the economy call threw, disposing the claim undoes the streak change
        return new DailyClaimResult(day, newStreak, reward, result.State);
    }
}

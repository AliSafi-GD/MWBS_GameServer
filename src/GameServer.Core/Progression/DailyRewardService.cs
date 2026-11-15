using GameServer.Abstractions;
using Npgsql;

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
    private readonly NpgsqlDataSource _db;
    private readonly IEconomy _economy;
    private readonly IConfigStore _config;
    private readonly HookRunner _hooks;
    private readonly TimeProvider _time;

    public DailyRewardService(NpgsqlDataSource db, IEconomy economy, IConfigStore config, HookRunner hooks, TimeProvider time)
    {
        _db = db;
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

        int streak = 0;
        DateOnly? last = null;
        await using (var cmd = _db.CreateCommand("SELECT streak, last_date FROM daily_rewards WHERE user_id = @u"))
        {
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                streak = reader.GetInt32(0);
                last = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1);
            }
        }

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

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await using (var ensure = new NpgsqlCommand("INSERT INTO daily_rewards (user_id) VALUES (@u) ON CONFLICT DO NOTHING", conn, tx))
            {
                ensure.Parameters.AddWithValue("u", userId);
                await ensure.ExecuteNonQueryAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }

        int streak;
        DateOnly? last;
        // The row stays locked until the commit, so two phones cannot both claim the same day.
        await using (var sel = new NpgsqlCommand("SELECT streak, last_date FROM daily_rewards WHERE user_id = @u FOR UPDATE", conn, tx))
        {
            sel.Parameters.AddWithValue("u", userId);
            await using var reader = await sel.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            streak = reader.GetInt32(0);
            last = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1);
        }

        if (last == today)
            throw new GameException("daily_already_claimed", "Today's reward was already taken.");

        var newStreak = DailyStreak.AfterClaim(streak, last, today, catalog.ResetOnMiss);
        var day = DailyStreak.RewardDay(newStreak, catalog.Rewards.Count);
        var reward = catalog.Rewards[day - 1];

        await using (var upd = new NpgsqlCommand("UPDATE daily_rewards SET streak = @s, last_date = @d WHERE user_id = @u", conn, tx))
        {
            upd.Parameters.AddWithValue("s", newStreak);
            upd.Parameters.AddWithValue("d", today);
            upd.Parameters.AddWithValue("u", userId);
            await upd.ExecuteNonQueryAsync(ct);
        }

        var transaction = new EconomyTransaction("daily_reward", $"daily:{today:yyyy-MM-dd}");
        reward.AddTo(transaction, +1);
        var result = await _economy.ApplyAsync(userId, transaction, ct);

        await tx.CommitAsync(ct); // if the economy call threw, the streak change is rolled back with the disposed transaction
        return new DailyClaimResult(day, newStreak, reward, result.State);
    }
}

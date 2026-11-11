using GameServer.Abstractions;
using Npgsql;

namespace GameServer.Core.Progression;

public sealed record DailyClaimRequest;

public sealed record DailyView(
    int Streak, bool ClaimedToday, int NextDay, IReadOnlyList<Bundle> Rewards, DateTime NextClaimUtc, int UtcOffsetMinutes);

public sealed record DailyClaimResult(int Day, int Streak, Bundle Reward, EconomyState State);

/// <summary>Daily login reward with a streak (config key "daily_rewards").</summary>
public sealed class DailyRewardService
{
    private readonly NpgsqlDataSource _db;
    private readonly IEconomy _economy;
    private readonly IConfigStore _config;
    private readonly HookRunner _hooks;

    public DailyRewardService(NpgsqlDataSource db, IEconomy economy, IConfigStore config, HookRunner hooks)
    {
        _db = db;
        _economy = economy;
        _config = config;
        _hooks = hooks;
    }

    public async Task<DailyCatalog> CatalogAsync(CancellationToken ct) =>
        DailyCatalog.From(await _config.GetAsync(ConfigRules.DailyRewards, ct));

    private static DateOnly TodayOf(DailyCatalog catalog, DateTime nowUtc) =>
        DateOnly.FromDateTime(nowUtc.AddMinutes(catalog.UtcOffsetMinutes));

    public async Task<DailyView> GetAsync(string userId, CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        var now = DateTime.UtcNow;
        var today = TodayOf(catalog, now);

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
        var effective = claimedToday || last == today.AddDays(-1) || !catalog.ResetOnMiss ? streak : 0;
        var n = Math.Max(catalog.Rewards.Count, 1);
        var nextDay = effective % n + 1;
        var nextClaim = new DateTime(today.AddDays(1).Year, today.AddDays(1).Month, today.AddDays(1).Day, 0, 0, 0, DateTimeKind.Utc)
            .AddMinutes(-catalog.UtcOffsetMinutes);

        return new DailyView(effective, claimedToday, nextDay, catalog.Rewards, claimedToday ? nextClaim : now, catalog.UtcOffsetMinutes);
    }

    public Task<DailyClaimResult> ClaimAsync(string userId, CancellationToken ct) =>
        _hooks.RunAsync(HookOps.DailyClaim, userId, new DailyClaimRequest(), _ => DoClaimAsync(userId, ct), ct);

    private async Task<DailyClaimResult> DoClaimAsync(string userId, CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        if (catalog.Rewards.Count == 0)
            throw new GameException("daily_not_configured", "This game has no daily reward.");

        var today = TodayOf(catalog, DateTime.UtcNow);

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

        var newStreak = last is null ? 1
            : last == today.AddDays(-1) ? streak + 1
            : catalog.ResetOnMiss ? 1
            : streak + 1;

        var day = (newStreak - 1) % catalog.Rewards.Count + 1;
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

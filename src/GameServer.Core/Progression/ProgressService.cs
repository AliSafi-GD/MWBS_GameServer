using GameServer.Abstractions;
using GameServer.Core.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GameServer.Core.Progression;

public sealed record GoalClaimRequest(string GoalId);

/// <summary>What a player sees of an achievement or quest.</summary>
public sealed record GoalView(
    string Id, string Name, string? Description, string? Category, string Stat, long Target, long Progress,
    bool Completed, bool Claimed, Bundle Reward, string Period, DateTime? ResetsUtc);

public sealed record GoalClaimResult(string Id, Bundle Reward, EconomyState State);

/// <summary>Stats, achievements and quests.</summary>
public sealed class ProgressService : IProgress
{
    private const long MaxDelta = 1_000_000_000_000;

    private sealed record Catalogs(GoalCatalog Achievements, GoalCatalog Quests, DateTime LoadedUtc);

    private readonly NpgsqlDataSource _db;
    private readonly IEconomy _economy;
    private readonly IConfigStore _config;
    private readonly HookRunner _hooks;
    private readonly IServiceProvider _services;
    private readonly ILogger<ProgressService> _log;
    private volatile Catalogs? _catalogs;

    public ProgressService(
        NpgsqlDataSource db, IEconomy economy, IConfigStore config, HookRunner hooks, IServiceProvider services,
        ILogger<ProgressService> log)
    {
        _db = db;
        _economy = economy;
        _config = config;
        _hooks = hooks;
        _services = services;
        _log = log;
    }

    /// <summary>The definitions, cached for a few seconds because every stat change looks at them.</summary>
    public async Task<(GoalCatalog Achievements, GoalCatalog Quests)> CatalogsAsync(CancellationToken ct, bool fresh = false)
    {
        var cached = _catalogs;
        if (!fresh && cached is not null && (DateTime.UtcNow - cached.LoadedUtc).TotalSeconds < 5)
            return (cached.Achievements, cached.Quests);

        var achievements = GoalCatalog.From(GoalCatalog.Achievement, await _config.GetAsync(ConfigRules.Achievements, ct));
        var quests = GoalCatalog.From(GoalCatalog.Quest, await _config.GetAsync(ConfigRules.Quests, ct));
        _catalogs = new Catalogs(achievements, quests, DateTime.UtcNow);
        return (achievements, quests);
    }

    // ---- stats ----------------------------------------------------------------------------------------------

    public Task<long> AddStatAsync(string userId, string stat, long delta = 1, CancellationToken ct = default)
    {
        if (delta is < 1 or > MaxDelta)
            throw new GameException("invalid_stat", "delta must be from 1 to 1000000000000.");
        return ChangeAsync(userId, stat, delta, max: false, ct);
    }

    public Task<long> SetStatMaxAsync(string userId, string stat, long value, CancellationToken ct = default)
    {
        if (value is < 0 or > MaxDelta)
            throw new GameException("invalid_stat", "value must be from 0 to 1000000000000.");
        return ChangeAsync(userId, stat, value, max: true, ct);
    }

    private async Task<long> ChangeAsync(string userId, string stat, long amount, bool max, CancellationToken ct)
    {
        if (!ConfigRules.IsValidName(stat))
            throw new GameException("invalid_stat", "A stat name is 1-64 characters: lowercase letters, digits, '_', '-' or '.'.");

        var now = DateTime.UtcNow;
        var keys = PeriodKeys.All.Select(p => PeriodKeys.Key(p, now)).ToArray();

        // The old values are read in the same statement, so two parallel changes cannot both miss a goal being reached.
        var sql = $"""
            WITH old AS (
                SELECT period_key, value FROM player_stats WHERE user_id = @u AND name = @n AND period_key = ANY(@keys)
            )
            INSERT INTO player_stats (user_id, name, period_key, value)
            SELECT @u, @n, k, @a FROM unnest(@keys) AS k
            ON CONFLICT (user_id, name, period_key)
            DO UPDATE SET value = {(max ? "GREATEST(player_stats.value, EXCLUDED.value)" : "player_stats.value + EXCLUDED.value")}
            RETURNING period_key, value, COALESCE((SELECT o.value FROM old o WHERE o.period_key = player_stats.period_key), 0)
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("n", stat);
        cmd.Parameters.AddWithValue("a", amount);
        cmd.Parameters.AddWithValue("keys", keys);

        var changes = new Dictionary<string, (long Old, long New)>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                changes[reader.GetString(0)] = (reader.GetInt64(2), reader.GetInt64(1));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }

        await AnnounceReachedGoalsAsync(userId, stat, changes, now, ct);
        return changes.TryGetValue("all", out var life) ? life.New : 0;
    }

    private async Task AnnounceReachedGoalsAsync(
        string userId, string stat, Dictionary<string, (long Old, long New)> changes, DateTime now, CancellationToken ct)
    {
        try
        {
            var (achievements, quests) = await CatalogsAsync(ct);
            var reached = new List<(string Kind, GoalDef Goal)>();
            foreach (var (catalog, kind) in new[] { (achievements, GoalCatalog.Achievement), (quests, GoalCatalog.Quest) })
            {
                foreach (var goal in catalog.Goals.Values.Where(g => g.Stat == stat))
                {
                    if (changes.TryGetValue(PeriodKeys.Key(goal.Period, now), out var c) && c.Old < goal.Target && c.New >= goal.Target)
                        reached.Add((kind, goal));
                }
            }

            if (reached.Count == 0)
                return;

            var realtime = _services.GetService<ConnectionManager>();
            if (realtime is null)
                return;

            foreach (var (kind, goal) in reached)
            {
                await realtime.SendToUserAsync(userId, Frames.Build("goal.completed", null,
                    new { kind, id = goal.Id, name = goal.Name, hasReward = !goal.Reward.IsEmpty }), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Announcing reached goals failed; the stat itself was saved.");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> GetStatsAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT name, value FROM player_stats WHERE user_id = @u AND period_key = 'all' ORDER BY name LIMIT 1000");
        cmd.Parameters.AddWithValue("u", userId);

        var result = new Dictionary<string, long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    // ---- listing --------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<GoalView>> ListAsync(string kind, string userId, CancellationToken ct)
    {
        var (achievements, quests) = await CatalogsAsync(ct, fresh: true);
        var catalog = kind == GoalCatalog.Achievement ? achievements : quests;
        if (catalog.Goals.Count == 0)
            return Array.Empty<GoalView>();

        var now = DateTime.UtcNow;
        var keys = PeriodKeys.All.Select(p => PeriodKeys.Key(p, now)).ToArray();

        var values = new Dictionary<(string Stat, string Key), long>();
        await using (var cmd = _db.CreateCommand(
            "SELECT name, period_key, value FROM player_stats WHERE user_id = @u AND period_key = ANY(@keys) AND name = ANY(@names)"))
        {
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("keys", keys);
            cmd.Parameters.AddWithValue("names", catalog.Goals.Values.Select(g => g.Stat).Distinct().ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                values[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        }

        var claimed = new HashSet<(string Goal, string Key)>();
        await using (var cmd = _db.CreateCommand(
            "SELECT goal_id, period_key FROM goal_claims WHERE user_id = @u AND kind = @k AND period_key = ANY(@keys)"))
        {
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("keys", keys);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                claimed.Add((reader.GetString(0), reader.GetString(1)));
        }

        var views = new List<GoalView>();
        foreach (var goal in catalog.Goals.Values.OrderBy(g => g.Sort).ThenBy(g => g.Id, StringComparer.Ordinal))
        {
            var key = PeriodKeys.Key(goal.Period, now);
            var value = values.GetValueOrDefault((goal.Stat, key));
            var done = value >= goal.Target;
            var isClaimed = claimed.Contains((goal.Id, key));
            if (goal.Hidden && !done && !isClaimed)
                continue; // secret achievements show up once reached

            views.Add(new GoalView(goal.Id, goal.Name, goal.Description, goal.Category, goal.Stat, goal.Target,
                Math.Min(value, goal.Target), done, isClaimed, goal.Reward, goal.Period, PeriodKeys.NextReset(goal.Period, now)));
        }

        return views;
    }

    // ---- claiming -------------------------------------------------------------------------------------------

    public Task<GoalClaimResult> ClaimAsync(string kind, string userId, string goalId, CancellationToken ct)
    {
        var op = kind == GoalCatalog.Achievement ? HookOps.AchievementsClaim : HookOps.QuestsClaim;
        return _hooks.RunAsync(op, userId, new GoalClaimRequest(goalId), r => DoClaimAsync(kind, userId, r.GoalId, ct), ct);
    }

    private async Task<GoalClaimResult> DoClaimAsync(string kind, string userId, string goalId, CancellationToken ct)
    {
        var (achievements, quests) = await CatalogsAsync(ct, fresh: true);
        var catalog = kind == GoalCatalog.Achievement ? achievements : quests;
        if (!catalog.Goals.TryGetValue(goalId ?? "", out var goal))
            throw new GameException("goal_not_found", "No such " + kind + ".");

        var now = DateTime.UtcNow;
        var periodKey = PeriodKeys.Key(goal.Period, now);

        long value;
        await using (var cmd = _db.CreateCommand(
            "SELECT COALESCE((SELECT value FROM player_stats WHERE user_id = @u AND name = @n AND period_key = @k), 0)"))
        {
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("n", goal.Stat);
            cmd.Parameters.AddWithValue("k", periodKey);
            value = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        if (value < goal.Target)
            throw new GameException("goal_not_complete", "Not completed yet.");

        // Mark it as taken first: the primary key lets only one request win.
        await using (var cmd = _db.CreateCommand("""
            INSERT INTO goal_claims (user_id, kind, goal_id, period_key) VALUES (@u, @kind, @g, @k)
            ON CONFLICT DO NOTHING RETURNING 1
            """))
        {
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("g", goal.Id);
            cmd.Parameters.AddWithValue("k", periodKey);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                throw new GameException("already_claimed", "The reward was already taken.");
        }

        try
        {
            if (goal.Reward.IsEmpty)
                return new GoalClaimResult(goal.Id, goal.Reward, await _economy.GetAsync(userId, ct));

            var tx = new EconomyTransaction(kind + "_reward", $"{kind}:{goal.Id}:{periodKey}");
            goal.Reward.AddTo(tx, +1);
            var result = await _economy.ApplyAsync(userId, tx, ct);
            return new GoalClaimResult(goal.Id, goal.Reward, result.State);
        }
        catch
        {
            await UnclaimAsync(kind, userId, goal.Id, periodKey);
            throw;
        }
    }

    private async Task UnclaimAsync(string kind, string userId, string goalId, string periodKey)
    {
        try
        {
            await using var cmd = _db.CreateCommand(
                "DELETE FROM goal_claims WHERE user_id = @u AND kind = @kind AND goal_id = @g AND period_key = @k");
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("g", goalId);
            cmd.Parameters.AddWithValue("k", periodKey);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch
        {
            // Best effort: at worst the player has to ask support for the reward.
        }
    }
}

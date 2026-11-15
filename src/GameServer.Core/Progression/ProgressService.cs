using GameServer.Abstractions;
using GameServer.Core.Realtime;
using Microsoft.Extensions.Logging;

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

    private readonly IProgressStore _store;
    private readonly IEconomy _economy;
    private readonly IConfigStore _config;
    private readonly HookRunner _hooks;
    private readonly IPlayerPush _push;
    private readonly ILogger<ProgressService> _log;
    private readonly TimeProvider _time;
    private volatile Catalogs? _catalogs;

    public ProgressService(
        IProgressStore store, IEconomy economy, IConfigStore config, HookRunner hooks, IPlayerPush push,
        ILogger<ProgressService> log, TimeProvider time)
    {
        _store = store;
        _economy = economy;
        _config = config;
        _hooks = hooks;
        _push = push;
        _log = log;
        _time = time;
    }

    /// <summary>The definitions, cached for a few seconds because every stat change looks at them.</summary>
    public async Task<(GoalCatalog Achievements, GoalCatalog Quests)> CatalogsAsync(CancellationToken ct, bool fresh = false)
    {
        var cached = _catalogs;
        if (!fresh && cached is not null && (_time.UtcNow() - cached.LoadedUtc).TotalSeconds < 5)
            return (cached.Achievements, cached.Quests);

        var achievements = GoalCatalog.From(GoalCatalog.Achievement, await _config.GetAsync(ConfigRules.Achievements, ct));
        var quests = GoalCatalog.From(GoalCatalog.Quest, await _config.GetAsync(ConfigRules.Quests, ct));
        _catalogs = new Catalogs(achievements, quests, _time.UtcNow());
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

        var now = _time.UtcNow();
        var keys = PeriodKeys.All.Select(p => PeriodKeys.Key(p, now)).ToArray();

        var changes = await _store.ChangeStatAsync(userId, stat, keys, amount, max, ct);

        await AnnounceReachedGoalsAsync(userId, stat, changes, now, ct);
        return changes.TryGetValue("all", out var life) ? life.New : 0;
    }

    private async Task AnnounceReachedGoalsAsync(
        string userId, string stat, IReadOnlyDictionary<string, StatChange> changes, DateTime now, CancellationToken ct)
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

            foreach (var (kind, goal) in reached)
            {
                await _push.SendToUserAsync(userId, Frames.Build("goal.completed", null,
                    new { kind, id = goal.Id, name = goal.Name, hasReward = !goal.Reward.IsEmpty }), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Announcing reached goals failed; the stat itself was saved.");
        }
    }

    public Task<IReadOnlyDictionary<string, long>> GetStatsAsync(string userId, CancellationToken ct = default) =>
        _store.LifetimeStatsAsync(userId, 1000, ct);

    // ---- listing --------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<GoalView>> ListAsync(string kind, string userId, CancellationToken ct)
    {
        var (achievements, quests) = await CatalogsAsync(ct, fresh: true);
        var catalog = kind == GoalCatalog.Achievement ? achievements : quests;
        if (catalog.Goals.Count == 0)
            return Array.Empty<GoalView>();

        var now = _time.UtcNow();
        var keys = PeriodKeys.All.Select(p => PeriodKeys.Key(p, now)).ToArray();

        var values = await _store.StatValuesAsync(userId, catalog.Goals.Values.Select(g => g.Stat).Distinct().ToArray(), keys, ct);
        var claimed = await _store.ClaimsAsync(userId, kind, keys, ct);

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

        var now = _time.UtcNow();
        var periodKey = PeriodKeys.Key(goal.Period, now);

        var values = await _store.StatValuesAsync(userId, new[] { goal.Stat }, new[] { periodKey }, ct);
        var value = values.GetValueOrDefault((goal.Stat, periodKey));

        if (value < goal.Target)
            throw new GameException("goal_not_complete", "Not completed yet.");

        // Mark it as taken first: only one request can win.
        if (!await _store.TryClaimAsync(userId, kind, goal.Id, periodKey, ct))
            throw new GameException("already_claimed", "The reward was already taken.");

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
            await _store.UnclaimAsync(userId, kind, goalId, periodKey, CancellationToken.None);
        }
        catch
        {
            // Best effort: at worst the player has to ask support for the reward.
        }
    }
}

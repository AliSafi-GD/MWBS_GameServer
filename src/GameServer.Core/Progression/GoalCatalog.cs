using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Progression;

/// <summary>An achievement or a quest: reach <see cref="Target"/> on a stat, then claim the reward.</summary>
public sealed record GoalDef(
    string Id, string Name, string? Description, string? Category, string Stat, long Target,
    string Period, Bundle Reward, bool Hidden, int Sort);

/// <summary>
/// Config keys "achievements" and "quests":
/// <code>
/// { "achievements": { "win_10": { "name": "Ten wins", "stat": "matches_won", "target": 10,
///     "reward": { "wallet": { "gems": 20 } }, "category": "pvp", "hidden": false, "sort": 1 } } }
/// { "quests": { "play_3": { "name": "Play 3 matches", "stat": "matches_played", "target": 3, "period": "day",
///     "reward": { "wallet": { "coins": 100 } } } } }
/// </code>
/// An achievement counts the stat for the whole lifetime; a quest only inside its period (day, week, month) and can be
/// claimed again in the next period.
/// </summary>
public sealed class GoalCatalog
{
    public const string Achievement = "achievement";
    public const string Quest = "quest";
    public const int MaxGoals = 500;

    public static readonly GoalCatalog EmptyAchievements = new(Achievement, new Dictionary<string, GoalDef>());
    public static readonly GoalCatalog EmptyQuests = new(Quest, new Dictionary<string, GoalDef>());

    private GoalCatalog(string kind, IReadOnlyDictionary<string, GoalDef> goals)
    {
        Kind = kind;
        Goals = goals;
    }

    public string Kind { get; }
    public IReadOnlyDictionary<string, GoalDef> Goals { get; }

    public static GoalCatalog From(string kind, ConfigEntry? entry) =>
        entry is null ? (kind == Achievement ? EmptyAchievements : EmptyQuests) : Parse(kind, entry.Value);

    public static GoalCatalog Parse(string kind, JsonElement root)
    {
        var section = kind == Achievement ? "achievements" : "quests";
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid($"{section} must be an object with \"{section}\".");

        var goals = new Dictionary<string, GoalDef>(StringComparer.Ordinal);
        if (root.TryGetProperty(section, out var list))
        {
            if (list.ValueKind != JsonValueKind.Object)
                throw Invalid($"{section}.{section} must be an object keyed by id.");

            foreach (var entry in list.EnumerateObject())
            {
                if (goals.Count >= MaxGoals)
                    throw Invalid($"At most {MaxGoals} entries are allowed.");
                if (!ConfigRules.IsValidName(entry.Name))
                    throw Invalid($"Invalid id '{entry.Name}'.");
                goals[entry.Name] = ParseGoal(kind, section, entry.Name, entry.Value);
            }
        }

        return new GoalCatalog(kind, goals);
    }

    private static GoalDef ParseGoal(string kind, string section, string id, JsonElement g)
    {
        var path = $"{section}.{section}.{id}";
        if (g.ValueKind != JsonValueKind.Object)
            throw Invalid($"{path} must be an object.");

        if (!g.TryGetProperty("stat", out var statEl) || statEl.ValueKind != JsonValueKind.String
            || !ConfigRules.IsValidName(statEl.GetString() ?? ""))
            throw Invalid($"{path}.stat is required (a stat name such as matches_won).");

        if (!g.TryGetProperty("target", out var targetEl) || !targetEl.TryGetInt64(out var target) || target is < 1 or > 1_000_000_000_000)
            throw Invalid($"{path}.target must be a positive whole number.");

        var period = "lifetime";
        if (kind == Quest)
        {
            period = "day";
            if (g.TryGetProperty("period", out var per))
            {
                period = per.ValueKind == JsonValueKind.String ? per.GetString() ?? "" : "";
                if (period is not ("day" or "week" or "month"))
                    throw Invalid($"{path}.period must be day, week or month.");
            }
        }

        var hidden = false;
        if (g.TryGetProperty("hidden", out var h))
        {
            if (h.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid($"{path}.hidden must be true or false.");
            hidden = h.GetBoolean();
        }

        var sort = 0;
        if (g.TryGetProperty("sort", out var s) && !s.TryGetInt32(out sort))
            throw Invalid($"{path}.sort must be a whole number.");

        var reward = g.TryGetProperty("reward", out var r) ? Bundle.Parse(path + ".reward", r) : Bundle.Empty;

        return new GoalDef(id, Text(g, "name", 100, path) ?? id, Text(g, "description", 500, path), Text(g, "category", 64, path),
            statEl.GetString()!, target, period, reward, hidden, sort);
    }

    private static string? Text(JsonElement o, string name, int max, string path)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        if (v.ValueKind != JsonValueKind.String || (v.GetString() ?? "").Length > max)
            throw Invalid($"{path}.{name} must be a text of at most {max} characters.");
        return v.GetString();
    }

    private static GameException Invalid(string message) => new("invalid_config", message);
}

/// <summary>
/// Config key "daily_rewards": the reward of each day of a login streak; after the last one the cycle starts again.
/// <code>
/// { "utcOffsetMinutes": 210, "resetOnMiss": true,
///   "rewards": [ { "wallet": { "coins": 100 } }, { "wallet": { "coins": 150 } }, { "items": { "chest": 1 } } ] }
/// </code>
/// "utcOffsetMinutes" moves the start of the day (210 = Iran). "resetOnMiss" false keeps the streak when a day is skipped.
/// </summary>
public sealed class DailyCatalog
{
    public static readonly DailyCatalog Empty = new(Array.Empty<Bundle>(), true, 0);

    private DailyCatalog(IReadOnlyList<Bundle> rewards, bool resetOnMiss, int utcOffsetMinutes)
    {
        Rewards = rewards;
        ResetOnMiss = resetOnMiss;
        UtcOffsetMinutes = utcOffsetMinutes;
    }

    public IReadOnlyList<Bundle> Rewards { get; }
    public bool ResetOnMiss { get; }
    public int UtcOffsetMinutes { get; }

    public static DailyCatalog From(ConfigEntry? entry) => entry is null ? Empty : Parse(entry.Value);

    public static DailyCatalog Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new GameException("invalid_config", "daily_rewards must be an object with \"rewards\".");

        var offset = 0;
        if (root.TryGetProperty("utcOffsetMinutes", out var o) && (!o.TryGetInt32(out offset) || offset is < -840 or > 840))
            throw new GameException("invalid_config", "daily_rewards.utcOffsetMinutes must be a whole number from -840 to 840.");

        var reset = true;
        if (root.TryGetProperty("resetOnMiss", out var r))
        {
            if (r.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GameException("invalid_config", "daily_rewards.resetOnMiss must be true or false.");
            reset = r.GetBoolean();
        }

        var rewards = new List<Bundle>();
        if (root.TryGetProperty("rewards", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 366)
                throw new GameException("invalid_config", "daily_rewards.rewards must be a list of at most 366 rewards.");

            var day = 0;
            foreach (var item in list.EnumerateArray())
            {
                day++;
                var bundle = Bundle.Parse($"daily_rewards.rewards[{day}]", item);
                if (bundle.IsEmpty)
                    throw new GameException("invalid_config", $"daily_rewards.rewards[{day}] needs a wallet or items amount.");
                rewards.Add(bundle);
            }
        }

        return new DailyCatalog(rewards, reset, offset);
    }
}

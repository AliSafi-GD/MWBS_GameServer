using System.Globalization;
using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core;

/// <summary>What currencies and items exist. Built from config key "economy". Absent sections are not enforced.</summary>
public sealed class EconomyCatalog
{
    private readonly Dictionary<string, long?>? _currencies;
    private readonly Dictionary<string, long?>? _items;

    private EconomyCatalog(Dictionary<string, long?>? currencies, Dictionary<string, long?>? items)
    {
        _currencies = currencies;
        _items = items;
    }

    /// <summary>Null when the config key does not exist (nothing is enforced).</summary>
    public static EconomyCatalog? From(ConfigEntry? entry) => entry is null ? null : Parse(entry.Value);

    /// <summary>Strict parse used for validation when an admin saves the key. Throws GameException("invalid_config").</summary>
    public static EconomyCatalog Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw GameDefinitions.Invalid("economy must be an object with \"currencies\" and/or \"items\".");

        return new EconomyCatalog(Section(root, "currencies", "max"), Section(root, "items", "stack"));
    }

    private static Dictionary<string, long?>? Section(JsonElement root, string name, string limitProperty)
    {
        if (!root.TryGetProperty(name, out var section))
            return null;
        if (section.ValueKind != JsonValueKind.Object)
            throw GameDefinitions.Invalid($"economy.{name} must be an object keyed by name.");

        var result = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var entry in section.EnumerateObject())
        {
            if (!ConfigRules.IsValidName(entry.Name))
                throw GameDefinitions.Invalid($"economy.{name}: invalid name '{entry.Name}'.");
            if (entry.Value.ValueKind != JsonValueKind.Object)
                throw GameDefinitions.Invalid($"economy.{name}.{entry.Name} must be an object.");

            long? limit = null;
            if (entry.Value.TryGetProperty(limitProperty, out var l))
            {
                if (!l.TryGetInt64(out var n) || n <= 0)
                    throw GameDefinitions.Invalid($"economy.{name}.{entry.Name}.{limitProperty} must be a positive whole number.");
                limit = n;
            }
            result[entry.Name] = limit;
        }
        return result;
    }

    /// <summary>Throws unknown_currency / unknown_item for names that are not in the catalog.</summary>
    public void Check(IEnumerable<string> currencies, IEnumerable<string> items)
    {
        if (_currencies is not null)
            foreach (var c in currencies)
                if (!_currencies.ContainsKey(c))
                    throw new GameException("unknown_currency", $"'{c}' is not a currency of this game.");

        if (_items is not null)
            foreach (var i in items)
                if (!_items.ContainsKey(i))
                    throw new GameException("unknown_item", $"'{i}' is not an item of this game.");
    }

    /// <summary>Highest allowed balance of a currency (config "max"), or null for no limit.</summary>
    public long? CurrencyLimit(string name) => _currencies is not null && _currencies.TryGetValue(name, out var v) ? v : null;

    /// <summary>Highest allowed quantity of an item (config "stack"), or null for no limit.</summary>
    public long? ItemLimit(string id) => _items is not null && _items.TryGetValue(id, out var v) ? v : null;
}

public sealed record LeaderboardDef(string Id, bool Descending, string Operator, string Reset);

public sealed record TournamentReward(
    int FromRank, int ToRank, IReadOnlyDictionary<string, long> Wallet, IReadOnlyDictionary<string, long> Items);

public sealed record TournamentDef(
    string Id, DateTime StartUtc, DateTime EndUtc, bool Descending, string Operator,
    IReadOnlyDictionary<string, long> EntryFee, IReadOnlyList<TournamentReward> Rewards, JsonElement? Name);

/// <summary>Parsing and validation of the config keys "leaderboards" and "tournaments".</summary>
public static class GameDefinitions
{
    private const long MaxAmount = 1_000_000_000_000;

    internal static GameException Invalid(string message) => new("invalid_config", message);

    public static IReadOnlyDictionary<string, LeaderboardDef> ParseLeaderboards(JsonElement? root)
    {
        var result = new Dictionary<string, LeaderboardDef>(StringComparer.Ordinal);
        if (root is not { } r) return result;
        if (r.ValueKind != JsonValueKind.Object)
            throw Invalid("leaderboards must be an object keyed by leaderboard id.");

        foreach (var entry in r.EnumerateObject())
        {
            if (!ConfigRules.IsValidName(entry.Name))
                throw Invalid($"leaderboards: invalid id '{entry.Name}'.");
            if (entry.Value.ValueKind != JsonValueKind.Object)
                throw Invalid($"leaderboards.{entry.Name} must be an object.");

            result[entry.Name] = new LeaderboardDef(
                entry.Name,
                Sort(entry.Value, $"leaderboards.{entry.Name}"),
                Choice(entry.Value, "operator", "best", new[] { "best", "set", "incr" }, $"leaderboards.{entry.Name}"),
                Choice(entry.Value, "reset", "never", new[] { "never", "daily", "weekly", "monthly" }, $"leaderboards.{entry.Name}"));
        }
        return result;
    }

    public static IReadOnlyDictionary<string, TournamentDef> ParseTournaments(JsonElement? root)
    {
        var result = new Dictionary<string, TournamentDef>(StringComparer.Ordinal);
        if (root is not { } r) return result;
        if (r.ValueKind != JsonValueKind.Object)
            throw Invalid("tournaments must be an object keyed by tournament id.");

        foreach (var entry in r.EnumerateObject())
        {
            var where = $"tournaments.{entry.Name}";
            if (!ConfigRules.IsValidName(entry.Name))
                throw Invalid($"tournaments: invalid id '{entry.Name}'.");
            var t = entry.Value;
            if (t.ValueKind != JsonValueKind.Object)
                throw Invalid($"{where} must be an object.");

            var start = Time(t, "start", where);
            var end = Time(t, "end", where);
            if (end <= start)
                throw Invalid($"{where}: end must be after start.");

            var fee = t.TryGetProperty("entryFee", out var f) ? Amounts(f, $"{where}.entryFee") : new Dictionary<string, long>();

            var rewards = new List<TournamentReward>();
            if (t.TryGetProperty("rewards", out var rw))
            {
                if (rw.ValueKind != JsonValueKind.Array)
                    throw Invalid($"{where}.rewards must be an array.");
                foreach (var tier in rw.EnumerateArray())
                {
                    if (tier.ValueKind != JsonValueKind.Object
                        || !tier.TryGetProperty("fromRank", out var fromEl) || !fromEl.TryGetInt32(out var fromRank) || fromRank < 1
                        || !tier.TryGetProperty("toRank", out var toEl) || !toEl.TryGetInt32(out var toRank) || toRank < fromRank)
                        throw Invalid($"{where}.rewards: each tier needs whole numbers fromRank >= 1 and toRank >= fromRank.");

                    var wallet = tier.TryGetProperty("wallet", out var w) ? Amounts(w, $"{where}.rewards.wallet") : new Dictionary<string, long>();
                    var items = tier.TryGetProperty("items", out var it) ? Amounts(it, $"{where}.rewards.items") : new Dictionary<string, long>();
                    if (wallet.Count == 0 && items.Count == 0)
                        throw Invalid($"{where}.rewards: a tier needs wallet and/or items.");
                    rewards.Add(new TournamentReward(fromRank, toRank, wallet, items));
                }
            }

            result[entry.Name] = new TournamentDef(
                entry.Name, start, end,
                Sort(t, where),
                Choice(t, "operator", "best", new[] { "best", "set", "incr" }, where),
                fee, rewards,
                t.TryGetProperty("name", out var name) ? name.Clone() : null);
        }
        return result;
    }

    private static bool Sort(JsonElement obj, string where) =>
        Choice(obj, "sort", "desc", new[] { "desc", "asc" }, where) == "desc";

    private static string Choice(JsonElement obj, string property, string fallback, string[] allowed, string where)
    {
        if (!obj.TryGetProperty(property, out var v)) return fallback;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (text is null || !allowed.Contains(text))
            throw Invalid($"{where}.{property} must be one of: {string.Join(", ", allowed)}.");
        return text;
    }

    private static DateTime Time(JsonElement obj, string property, string where)
    {
        if (obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.UtcDateTime;
        throw Invalid($"{where}.{property} must be a date like \"2026-12-01T00:00:00Z\".");
    }

    private static Dictionary<string, long> Amounts(JsonElement obj, string where)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw Invalid($"{where} must be an object like {{ \"coins\": 100 }}.");

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in obj.EnumerateObject())
        {
            if (!ConfigRules.IsValidName(e.Name))
                throw Invalid($"{where}: invalid name '{e.Name}'.");
            if (!e.Value.TryGetInt64(out var n) || n <= 0 || n > MaxAmount)
                throw Invalid($"{where}.{e.Name} must be a positive whole number.");
            result[e.Name] = n;
        }
        return result;
    }
}

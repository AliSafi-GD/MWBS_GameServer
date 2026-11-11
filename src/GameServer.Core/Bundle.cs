using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core;

/// <summary>Currencies and items, for a price or a reward: <c>{ "wallet": { "coins": 100 }, "items": { "card_fire": 1 } }</c>.</summary>
public sealed record Bundle(IReadOnlyDictionary<string, long> Wallet, IReadOnlyDictionary<string, long> Items)
{
    public static readonly Bundle Empty = new(new Dictionary<string, long>(), new Dictionary<string, long>());
    public bool IsEmpty => Wallet.Count == 0 && Items.Count == 0;

    private const long MaxAmount = 1_000_000_000_000;

    /// <summary>Strict parse for config validation. Throws GameException("invalid_config") naming <paramref name="path"/>.</summary>
    public static Bundle Parse(string path, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new GameException("invalid_config", $"{path} must be an object with \"wallet\" and/or \"items\".");

        return new Bundle(Amounts(path, value, "wallet"), Amounts(path, value, "items"));
    }

    private static Dictionary<string, long> Amounts(string path, JsonElement bundle, string section)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!bundle.TryGetProperty(section, out var values))
            return result;
        if (values.ValueKind != JsonValueKind.Object)
            throw new GameException("invalid_config", $"{path}.{section} must be an object of name: amount.");

        foreach (var entry in values.EnumerateObject())
        {
            if (!ConfigRules.IsValidName(entry.Name))
                throw new GameException("invalid_config", $"{path}.{section}: invalid name '{entry.Name}'.");
            if (!entry.Value.TryGetInt64(out var amount) || amount is < 1 or > MaxAmount)
                throw new GameException("invalid_config", $"{path}.{section}.{entry.Name} must be a positive whole number.");
            result[entry.Name] = amount;
        }

        return result;
    }

    /// <summary>Adds this bundle to a transaction, <paramref name="sign"/> times <paramref name="times"/> (+1 grant, -1 spend).</summary>
    public void AddTo(EconomyTransaction tx, int sign, long times = 1)
    {
        foreach (var (name, amount) in Wallet) tx.Currency(name, sign * checked(amount * times));
        foreach (var (id, amount) in Items) tx.Item(id, sign * checked(amount * times));
    }
}

/// <summary>Calendar periods (all UTC) used by shop limits, quests and per-period stats.</summary>
public static class PeriodKeys
{
    /// <summary>"day", "week", "month" and "lifetime".</summary>
    public static readonly string[] All = { "day", "week", "month", "lifetime" };

    public static bool IsValid(string per) => All.Contains(per);

    public static string Key(string per, DateTime nowUtc) => per switch
    {
        "day" => nowUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        "week" => $"{System.Globalization.ISOWeek.GetYear(nowUtc):D4}-W{System.Globalization.ISOWeek.GetWeekOfYear(nowUtc):D2}",
        "month" => nowUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        _ => "all"
    };

    /// <summary>When the current period ends (null for lifetime). Weeks start on Monday.</summary>
    public static DateTime? NextReset(string per, DateTime nowUtc) => per switch
    {
        "day" => nowUtc.Date.AddDays(1),
        "week" => nowUtc.Date.AddDays(7 - ((int)nowUtc.DayOfWeek + 6) % 7),
        "month" => new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1),
        _ => null
    };
}

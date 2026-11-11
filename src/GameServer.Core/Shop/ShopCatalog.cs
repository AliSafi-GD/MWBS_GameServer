using System.Globalization;
using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Shop;

/// <summary>"per" is day, week, month (all UTC) or lifetime.</summary>
public sealed record PurchaseLimit(string Per, int Count);

public sealed record ShopProduct(
    string Id, string Name, string? Description, string? Category, int Sort, bool Enabled,
    Bundle Cost, Bundle Gives, PurchaseLimit? Limit, DateTime? StartUtc, DateTime? EndUtc)
{
    public bool IsActive(DateTime nowUtc) =>
        Enabled && (StartUtc is null || nowUtc >= StartUtc) && (EndUtc is null || nowUtc < EndUtc);
}

/// <summary>
/// The shop from config key "shop":
/// <code>
/// { "products": { "gems_small": {
///     "name": "Small gem pack", "category": "gems", "sort": 1,
///     "cost":  { "wallet": { "coins": 500 }, "items": { "ticket": 1 } },
///     "gives": { "wallet": { "gems": 50 },  "items": { "card_fire": 1 } },
///     "limit": { "per": "day", "count": 3 },
///     "startUtc": "2026-12-01T00:00:00Z", "endUtc": "2026-12-08T00:00:00Z", "enabled": true } } }
/// </code>
/// Only "gives" is required; a product without "cost" is free (use "limit" for a daily gift).
/// </summary>
public sealed class ShopCatalog
{
    public const int MaxProducts = 500;
    private const long MaxAmount = 1_000_000_000_000;

    public static readonly ShopCatalog Empty = new(new Dictionary<string, ShopProduct>());

    private ShopCatalog(IReadOnlyDictionary<string, ShopProduct> products) => Products = products;

    public IReadOnlyDictionary<string, ShopProduct> Products { get; }

    public static ShopCatalog From(ConfigEntry? entry) => entry is null ? Empty : Parse(entry.Value);

    /// <summary>Strict parse, also used to validate config writes. Throws GameException("invalid_config").</summary>
    public static ShopCatalog Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid("shop must be an object with \"products\".");

        var products = new Dictionary<string, ShopProduct>(StringComparer.Ordinal);
        if (root.TryGetProperty("products", out var list))
        {
            if (list.ValueKind != JsonValueKind.Object)
                throw Invalid("shop.products must be an object keyed by product id.");

            foreach (var entry in list.EnumerateObject())
            {
                if (products.Count >= MaxProducts)
                    throw Invalid($"A shop can have at most {MaxProducts} products.");
                if (!ConfigRules.IsValidName(entry.Name))
                    throw Invalid($"Invalid product id '{entry.Name}'.");
                products[entry.Name] = ParseProduct(entry.Name, entry.Value);
            }
        }

        return new ShopCatalog(products);
    }

    private static ShopProduct ParseProduct(string id, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
            throw Invalid($"shop.products.{id} must be an object.");

        var name = ReadString(p, "name", 100) ?? id;
        var description = ReadString(p, "description", 500);
        var category = ReadString(p, "category", 64);
        var sort = 0;
        if (p.TryGetProperty("sort", out var s) && (!s.TryGetInt32(out sort)))
            throw Invalid($"shop.products.{id}.sort must be a whole number.");

        var enabled = true;
        if (p.TryGetProperty("enabled", out var e))
        {
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid($"shop.products.{id}.enabled must be true or false.");
            enabled = e.GetBoolean();
        }

        var cost = p.TryGetProperty("cost", out var c) ? ParseBundle(id, "cost", c) : Bundle.Empty;
        if (!p.TryGetProperty("gives", out var g))
            throw Invalid($"shop.products.{id}.gives is required.");
        var gives = ParseBundle(id, "gives", g);
        if (gives.IsEmpty)
            throw Invalid($"shop.products.{id}.gives must contain at least one currency or item.");

        PurchaseLimit? limit = null;
        if (p.TryGetProperty("limit", out var l))
        {
            if (l.ValueKind != JsonValueKind.Object)
                throw Invalid($"shop.products.{id}.limit must be an object.");

            var per = ReadString(l, "per", 16) ?? "lifetime";
            if (per is not ("day" or "week" or "month" or "lifetime"))
                throw Invalid($"shop.products.{id}.limit.per must be day, week, month or lifetime.");
            if (!l.TryGetProperty("count", out var n) || !n.TryGetInt32(out var count) || count is < 1 or > 1_000_000)
                throw Invalid($"shop.products.{id}.limit.count must be a whole number from 1 to 1000000.");
            limit = new PurchaseLimit(per, count);
        }

        var start = ReadDate(p, id, "startUtc");
        var end = ReadDate(p, id, "endUtc");
        if (start is not null && end is not null && end <= start)
            throw Invalid($"shop.products.{id}: endUtc must be after startUtc.");

        return new ShopProduct(id, name, description, category, sort, enabled, cost, gives, limit, start, end);
    }

    private static Bundle ParseBundle(string id, string field, JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object)
            throw Invalid($"shop.products.{id}.{field} must be an object with \"wallet\" and/or \"items\".");

        return new Bundle(Amounts(id, field, b, "wallet"), Amounts(id, field, b, "items"));
    }

    private static Dictionary<string, long> Amounts(string id, string field, JsonElement b, string section)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!b.TryGetProperty(section, out var values))
            return result;
        if (values.ValueKind != JsonValueKind.Object)
            throw Invalid($"shop.products.{id}.{field}.{section} must be an object of name: amount.");

        foreach (var entry in values.EnumerateObject())
        {
            if (!ConfigRules.IsValidName(entry.Name))
                throw Invalid($"shop.products.{id}.{field}.{section}: invalid name '{entry.Name}'.");
            if (!entry.Value.TryGetInt64(out var amount) || amount is < 1 or > MaxAmount)
                throw Invalid($"shop.products.{id}.{field}.{section}.{entry.Name} must be a positive whole number.");
            result[entry.Name] = amount;
        }

        return result;
    }

    private static string? ReadString(JsonElement o, string name, int max)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        if (v.ValueKind != JsonValueKind.String || (v.GetString() ?? "").Length > max)
            throw Invalid($"\"{name}\" must be a text of at most {max} characters.");
        return v.GetString();
    }

    private static DateTime? ReadDate(JsonElement o, string id, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        if (v.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            throw Invalid($"shop.products.{id}.{name} must be a date like 2026-12-01T00:00:00Z.");
        return date;
    }

    private static GameException Invalid(string message) => new("invalid_config", message);
}

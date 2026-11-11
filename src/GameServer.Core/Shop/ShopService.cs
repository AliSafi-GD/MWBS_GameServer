using System.Globalization;
using GameServer.Abstractions;
using Npgsql;

namespace GameServer.Core.Shop;

public sealed record ShopBuyRequest(string ProductId, int? Quantity = null, string? RequestId = null);

/// <summary>What the player sees of a product, including how much of the limit is left for them.</summary>
public sealed record ShopProductView(
    string Id, string Name, string? Description, string? Category, Bundle Cost, Bundle Gives,
    PurchaseLimit? Limit, int? Remaining, bool Active, DateTime? StartUtc, DateTime? EndUtc);

public sealed record ShopPurchaseResult(string ProductId, int Quantity, bool AlreadyApplied, EconomyState State);

/// <summary>
/// Buying with in-game currency or items. The catalog is the config key "shop"; prices are always taken from it,
/// never from the client. Money moves through <see cref="IEconomy"/> (atomic, with history), so a purchase either
/// happens completely or not at all.
/// </summary>
public sealed class ShopService
{
    private readonly NpgsqlDataSource _db;
    private readonly IEconomy _economy;
    private readonly IConfigStore _config;
    private readonly HookRunner _hooks;

    public ShopService(NpgsqlDataSource db, IEconomy economy, IConfigStore config, HookRunner hooks)
    {
        _db = db;
        _economy = economy;
        _config = config;
        _hooks = hooks;
    }

    public async Task<ShopCatalog> CatalogAsync(CancellationToken ct) =>
        ShopCatalog.From(await _config.GetAsync(ConfigRules.Shop, ct));

    // ---- period keys ----------------------------------------------------------------------------------------

    internal static string PeriodKey(string per, DateTime nowUtc) => PeriodKeys.Key(per, nowUtc);

    private static readonly string[] Periods = { "day", "week", "month", "lifetime" };

    /// <summary>Purchases the player made in the current period of each kind: (product, period key) -> count.</summary>
    private async Task<Dictionary<(string Product, string Key), int>> CountsAsync(string userId, DateTime nowUtc, CancellationToken ct)
    {
        var keys = Periods.Select(p => PeriodKey(p, nowUtc)).ToArray();

        await using var cmd = _db.CreateCommand(
            "SELECT product_id, period_key, count FROM shop_purchases WHERE user_id = @u AND period_key = ANY(@k)");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", keys);

        var result = new Dictionary<(string, string), int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
        return result;
    }

    // ---- listing --------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ShopProductView>> ListAsync(string userId, string? category, CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        var now = DateTime.UtcNow;
        var counts = await CountsAsync(userId, now, ct);

        return catalog.Products.Values
            .Where(p => p.Enabled && (p.EndUtc is null || now < p.EndUtc))
            .Where(p => category is null || p.Category == category)
            .OrderBy(p => p.Sort).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new ShopProductView(
                p.Id, p.Name, p.Description, p.Category, p.Cost, p.Gives, p.Limit,
                RemainingOf(p, counts, now), p.IsActive(now), p.StartUtc, p.EndUtc))
            .ToArray();
    }

    private static int? RemainingOf(ShopProduct p, Dictionary<(string Product, string Key), int> counts, DateTime now)
    {
        if (p.Limit is null)
            return null;
        var used = counts.GetValueOrDefault((p.Id, PeriodKey(p.Limit.Per, now)));
        return Math.Max(p.Limit.Count - used, 0);
    }

    /// <summary>For the admin panel: what a player has bought in the current periods.</summary>
    public async Task<IReadOnlyList<object>> PurchasesOfAsync(string userId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT product_id, period_key, count FROM shop_purchases WHERE user_id = @u ORDER BY product_id, period_key DESC LIMIT 500");
        cmd.Parameters.AddWithValue("u", userId);

        var result = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new { productId = reader.GetString(0), period = reader.GetString(1), count = reader.GetInt32(2) });
        return result;
    }

    // ---- buying ---------------------------------------------------------------------------------------------

    public Task<ShopPurchaseResult> BuyAsync(string userId, ShopBuyRequest request, CancellationToken ct) =>
        _hooks.RunAsync(HookOps.ShopBuy, userId, request, r => DoBuyAsync(userId, r, ct), ct);

    private async Task<ShopPurchaseResult> DoBuyAsync(string userId, ShopBuyRequest request, CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct);
        if (!catalog.Products.TryGetValue(request.ProductId ?? "", out var product))
            throw new GameException("product_not_found", "No such product.");

        var now = DateTime.UtcNow;
        if (!product.IsActive(now))
            throw new GameException("product_unavailable", "This product is not on sale right now.");

        var quantity = request.Quantity ?? 1;
        if (quantity is < 1 or > 1000)
            throw new GameException("invalid_request", "quantity must be from 1 to 1000.");

        string? key = null;
        if (!string.IsNullOrEmpty(request.RequestId))
        {
            if (request.RequestId.Length > 64 || request.RequestId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
                throw new GameException("invalid_request", "requestId may contain letters, digits, '-' and '_' (at most 64).");
            key = $"shop:{product.Id}:{request.RequestId}";
        }

        // 1. Reserve the purchase against the limit. The conditional upsert is atomic, so two phones cannot both win.
        string? periodKey = null;
        if (product.Limit is { } limit)
        {
            if (quantity > limit.Count)
                throw new GameException("purchase_limit_reached", "That is more than the limit allows.");

            periodKey = PeriodKey(limit.Per, now);
            if (!await ReserveAsync(userId, product.Id, periodKey, quantity, limit.Count, ct))
                throw new GameException("purchase_limit_reached", "You cannot buy this again for now.");
        }

        // 2. Move the money. Anything that goes wrong gives the reservation back.
        try
        {
            var tx = new EconomyTransaction("shop_purchase", key);
            foreach (var (name, amount) in product.Cost.Wallet) tx.Currency(name, -checked(amount * quantity));
            foreach (var (id, amount) in product.Cost.Items) tx.Item(id, -checked(amount * quantity));
            foreach (var (name, amount) in product.Gives.Wallet) tx.Currency(name, checked(amount * quantity));
            foreach (var (id, amount) in product.Gives.Items) tx.Item(id, checked(amount * quantity));

            var result = await _economy.ApplyAsync(userId, tx, ct);

            if (result.AlreadyApplied && periodKey is not null)
                await ReleaseAsync(userId, product.Id, periodKey, quantity); // a retry of a purchase that already counted

            return new ShopPurchaseResult(product.Id, quantity, result.AlreadyApplied, result.State);
        }
        catch
        {
            if (periodKey is not null)
                await ReleaseAsync(userId, product.Id, periodKey, quantity);
            throw;
        }
    }

    private async Task<bool> ReserveAsync(string userId, string productId, string periodKey, int quantity, int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO shop_purchases (user_id, product_id, period_key, count)
            VALUES (@u, @p, @k, @q)
            ON CONFLICT (user_id, product_id, period_key)
            DO UPDATE SET count = shop_purchases.count + @q
            WHERE shop_purchases.count + @q <= @limit
            RETURNING count
            """);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("p", productId);
        cmd.Parameters.AddWithValue("k", periodKey);
        cmd.Parameters.AddWithValue("q", quantity);
        cmd.Parameters.AddWithValue("limit", limit);

        try
        {
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }
    }

    private async Task ReleaseAsync(string userId, string productId, string periodKey, int quantity)
    {
        try
        {
            await using var cmd = _db.CreateCommand(
                "UPDATE shop_purchases SET count = GREATEST(count - @q, 0) WHERE user_id = @u AND product_id = @p AND period_key = @k");
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("p", productId);
            cmd.Parameters.AddWithValue("k", periodKey);
            cmd.Parameters.AddWithValue("q", quantity);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch
        {
            // Giving the reservation back is best effort; the player at worst loses one use of a limited product.
        }
    }
}

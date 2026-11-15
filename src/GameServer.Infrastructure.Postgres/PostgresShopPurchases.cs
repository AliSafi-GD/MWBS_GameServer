using GameServer.Abstractions;
using GameServer.Core.Shop;
using Npgsql;

namespace GameServer.Infrastructure.Postgres;

/// <summary>Shop limit counters in table shop_purchases (user, product, period key) -> count.</summary>
public sealed class PostgresShopPurchases : IShopPurchaseStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresShopPurchases(NpgsqlDataSource db) => _db = db;

    public async Task<IReadOnlyDictionary<(string Product, string Key), int>> CountsAsync(
        string userId, IReadOnlyCollection<string> periodKeys, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT product_id, period_key, count FROM shop_purchases WHERE user_id = @u AND period_key = ANY(@k)");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", periodKeys.ToArray());

        var result = new Dictionary<(string, string), int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
        return result;
    }

    public async Task<IReadOnlyList<ShopPurchaseCount>> ListAsync(string userId, int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT product_id, period_key, count FROM shop_purchases WHERE user_id = @u ORDER BY product_id, period_key DESC LIMIT @l");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("l", limit);

        var result = new List<ShopPurchaseCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ShopPurchaseCount(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return result;
    }

    public async Task<bool> TryReserveAsync(string userId, string productId, string periodKey, int quantity, int limit, CancellationToken ct)
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

    public async Task ReleaseAsync(string userId, string productId, string periodKey, int quantity, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE shop_purchases SET count = GREATEST(count - @q, 0) WHERE user_id = @u AND product_id = @p AND period_key = @k");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("p", productId);
        cmd.Parameters.AddWithValue("k", periodKey);
        cmd.Parameters.AddWithValue("q", quantity);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

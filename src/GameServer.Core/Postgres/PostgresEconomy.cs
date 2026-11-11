using System.Text.RegularExpressions;
using GameServer.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

public sealed partial class PostgresEconomy : IEconomyStore
{
    private const long MaxDelta = 1_000_000_000_000;

    private readonly NpgsqlDataSource _db;
    private readonly IConfigStore _config;

    public PostgresEconomy(NpgsqlDataSource db, IConfigStore config)
    {
        _db = db;
        _config = config;
    }

    public async Task<EconomyResult> ApplyAsync(string userId, EconomyTransaction transaction, CancellationToken ct = default)
    {
        var currencies = Normalize(transaction.Currencies, "currency");
        var items = Normalize(transaction.Items, "item");

        var reason = (transaction.Reason ?? "").Trim();
        if (reason.Length is 0 or > 128)
            throw new GameException("invalid_transaction", "A reason is required (at most 128 characters).");

        var key = transaction.IdempotencyKey;
        if (key is not null && key.Length is 0 or > 128)
            throw new GameException("invalid_transaction", "The idempotency key must be 1-128 characters.");

        if (currencies.Count == 0 && items.Count == 0)
            throw new GameException("invalid_transaction", "The transaction changes nothing.");

        // If the game defined a catalog (config key "economy"), only its names and limits are allowed.
        var catalog = EconomyCatalog.From(await _config.GetAsync(ConfigRules.Economy, ct));
        catalog?.Check(currencies.Keys, items.Keys);

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // One transaction per player at a time: simple and removes every race between parallel requests.
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@u, 0))", conn, tx))
            {
                gate.Parameters.AddWithValue("u", userId);
                await gate.ExecuteNonQueryAsync(ct);
            }

            if (key is not null)
            {
                await using var seen = new NpgsqlCommand("SELECT 1 FROM ledger WHERE user_id = @u AND ref = @r LIMIT 1", conn, tx);
                seen.Parameters.AddWithValue("u", userId);
                seen.Parameters.AddWithValue("r", key);
                if (await seen.ExecuteScalarAsync(ct) is not null)
                {
                    var current = await ReadStateAsync(conn, tx, userId, ct);
                    await tx.CommitAsync(ct);
                    return new EconomyResult(true, current);
                }
            }

            foreach (var (name, delta) in currencies)
            {
                var balance = await ChangeAsync(conn, tx, userId, "wallet", name, delta, ct);
                if (delta > 0 && catalog?.CurrencyLimit(name) is { } maxBalance && balance > maxBalance)
                    throw new GameException("limit_reached", $"{name} cannot exceed {maxBalance}.");
                await WriteLedgerAsync(conn, tx, userId, "wallet", name, delta, balance, reason, key, ct);
            }

            foreach (var (name, delta) in items)
            {
                var quantity = await ChangeAsync(conn, tx, userId, "inventory", name, delta, ct);
                if (delta > 0 && catalog?.ItemLimit(name) is { } maxStack && quantity > maxStack)
                    throw new GameException("limit_reached", $"{name} cannot exceed {maxStack}.");
                await WriteLedgerAsync(conn, tx, userId, "inventory", name, delta, quantity, reason, key, ct);
            }

            var state = await ReadStateAsync(conn, tx, userId, ct);
            await tx.CommitAsync(ct);
            return new EconomyResult(false, state);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        {
            throw new GameException("invalid_transaction", "The amount is too large.");
        }
    }

    public async Task<EconomyState> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await ReadStateAsync(conn, null, userId, ct);
    }

    public async Task<IReadOnlyList<LedgerEntry>> LedgerAsync(string userId, int limit, long? beforeId, CancellationToken ct = default)
    {
        var result = new List<LedgerEntry>();
        await using var cmd = _db.CreateCommand("""
            SELECT id, kind, name, delta, balance_after, reason, ref, created_at
            FROM ledger
            WHERE user_id = @u AND (@before IS NULL OR id < @before)
            ORDER BY id DESC
            LIMIT @lim
            """);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Bigint) { Value = (object?)beforeId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new LedgerEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetDateTime(7)));
        }
        return result;
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    /// <summary>Adds delta to the balance and returns the new balance. The table's CHECK (>= 0) rejects overspending.</summary>
    private static async Task<long> ChangeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, string kind, string name, long delta, CancellationToken ct)
    {
        var (table, column, nameColumn) = kind == "wallet"
            ? ("wallet_balances", "balance", "currency")
            : ("inventory_items", "quantity", "item_id");

        // Spending: UPDATE only. (An INSERT ... ON CONFLICT would be rejected by the CHECK on the proposed negative
        // row before Postgres even looks for the conflict.) No row means there is nothing to spend.
        // Granting: upsert, the proposed row is positive so the CHECK passes.
        var sql = delta < 0
            ? $"UPDATE {table} SET {column} = {column} + @d, updated_at = now() WHERE user_id = @u AND {nameColumn} = @n RETURNING {column}"
            : $"""
              INSERT INTO {table} (user_id, {nameColumn}, {column}) VALUES (@u, @n, @d)
              ON CONFLICT (user_id, {nameColumn}) DO UPDATE
                 SET {column} = {table}.{column} + EXCLUDED.{column}, updated_at = now()
              RETURNING {column}
              """;

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("d", delta);

        try
        {
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is null)
                throw Insufficient(kind, name);
            return (long)value;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            throw Insufficient(kind, name);
        }
    }

    private static GameException Insufficient(string kind, string name) =>
        kind == "wallet"
            ? new GameException("insufficient_funds", $"Not enough {name}.")
            : new GameException("insufficient_items", $"Not enough {name}.");

    private static async Task WriteLedgerAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, string kind, string name,
        long delta, long balanceAfter, string reason, string? key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO ledger (user_id, kind, name, delta, balance_after, reason, ref)
            VALUES (@u, @k, @n, @d, @b, @why, @r)
            """, conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("d", delta);
        cmd.Parameters.AddWithValue("b", balanceAfter);
        cmd.Parameters.AddWithValue("why", reason);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = (object?)key ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<EconomyState> ReadStateAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string userId, CancellationToken ct)
    {
        var wallet = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT currency, balance FROM wallet_balances WHERE user_id = @u", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                wallet[reader.GetString(0)] = reader.GetInt64(1);
        }

        var items = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT item_id, quantity FROM inventory_items WHERE user_id = @u AND quantity > 0", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                items[reader.GetString(0)] = reader.GetInt64(1);
        }

        return new EconomyState(wallet, items);
    }

    private static SortedDictionary<string, long> Normalize(IReadOnlyDictionary<string, long> input, string what)
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, delta) in input)
        {
            if (!NameRegex().IsMatch(name))
                throw new GameException("invalid_transaction",
                    $"Invalid {what} name '{name}': use 1-64 lowercase letters, digits, '_', '-' or '.'.");
            if (delta is > MaxDelta or < -MaxDelta)
                throw new GameException("invalid_transaction", $"The amount of '{name}' is too large.");
            if (delta != 0)
                result[name] = delta;
        }
        return result;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.\-]{0,63}\z")]
    private static partial Regex NameRegex();
}

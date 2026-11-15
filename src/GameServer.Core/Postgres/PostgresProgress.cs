using GameServer.Abstractions;
using GameServer.Core.Progression;
using Npgsql;

namespace GameServer.Core.Postgres;

/// <summary>Stats in table player_stats (user, name, period key) -> value, and taken rewards in goal_claims.</summary>
public sealed class PostgresProgress : IProgressStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresProgress(NpgsqlDataSource db) => _db = db;

    public async Task<IReadOnlyDictionary<string, StatChange>> ChangeStatAsync(
        string userId, string stat, IReadOnlyCollection<string> periodKeys, long amount, bool max, CancellationToken ct)
    {
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
        cmd.Parameters.AddWithValue("keys", periodKeys.ToArray());

        var changes = new Dictionary<string, StatChange>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                changes[reader.GetString(0)] = new StatChange(reader.GetInt64(2), reader.GetInt64(1));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }

        return changes;
    }

    public async Task<IReadOnlyDictionary<string, long>> LifetimeStatsAsync(string userId, int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT name, value FROM player_stats WHERE user_id = @u AND period_key = 'all' ORDER BY name LIMIT @l");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("l", limit);

        var result = new Dictionary<string, long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    public async Task<IReadOnlyDictionary<(string Stat, string Key), long>> StatValuesAsync(
        string userId, IReadOnlyCollection<string> stats, IReadOnlyCollection<string> periodKeys, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT name, period_key, value FROM player_stats WHERE user_id = @u AND period_key = ANY(@keys) AND name = ANY(@names)");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("keys", periodKeys.ToArray());
        cmd.Parameters.AddWithValue("names", stats.ToArray());

        var values = new Dictionary<(string, string), long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            values[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        return values;
    }

    public async Task<IReadOnlySet<(string Goal, string Key)>> ClaimsAsync(
        string userId, string kind, IReadOnlyCollection<string> periodKeys, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT goal_id, period_key FROM goal_claims WHERE user_id = @u AND kind = @k AND period_key = ANY(@keys)");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("keys", periodKeys.ToArray());

        var claimed = new HashSet<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            claimed.Add((reader.GetString(0), reader.GetString(1)));
        return claimed;
    }

    public async Task<bool> TryClaimAsync(string userId, string kind, string goalId, string periodKey, CancellationToken ct)
    {
        // The primary key lets only one request win.
        await using var cmd = _db.CreateCommand("""
            INSERT INTO goal_claims (user_id, kind, goal_id, period_key) VALUES (@u, @kind, @g, @k)
            ON CONFLICT DO NOTHING RETURNING 1
            """);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("g", goalId);
        cmd.Parameters.AddWithValue("k", periodKey);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task UnclaimAsync(string userId, string kind, string goalId, string periodKey, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM goal_claims WHERE user_id = @u AND kind = @kind AND goal_id = @g AND period_key = @k");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("g", goalId);
        cmd.Parameters.AddWithValue("k", periodKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

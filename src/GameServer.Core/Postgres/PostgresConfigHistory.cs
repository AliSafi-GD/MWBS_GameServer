using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core.Admin;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

/// <summary>Old config values in table config_history.</summary>
public sealed class PostgresConfigHistory : IConfigHistoryStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresConfigHistory(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task AddConfigHistoryAsync(ConfigEntry old, string changedBy, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("INSERT INTO config_history (key, value, is_public, changed_by) VALUES (@k, @v, @p, @c)");
        cmd.Parameters.AddWithValue("k", old.Key);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = old.Value.GetRawText() });
        cmd.Parameters.AddWithValue("p", old.IsPublic);
        cmd.Parameters.AddWithValue("c", changedBy);
        await cmd.ExecuteNonQueryAsync(ct);

        // Keep the last 50 versions of each key.
        await using var trim = _db.CreateCommand(
            "DELETE FROM config_history WHERE key = @k AND id NOT IN (SELECT id FROM config_history WHERE key = @k ORDER BY id DESC LIMIT 50)");
        trim.Parameters.AddWithValue("k", old.Key);
        await trim.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ConfigHistoryEntry>> ConfigHistoryAsync(string key, int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT id, key, value, is_public, changed_by, at FROM config_history WHERE key = @k ORDER BY id DESC LIMIT @n");
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 50));
        return await ReadHistoryAsync(cmd, ct);
    }

    public async Task<ConfigHistoryEntry?> ConfigHistoryItemAsync(string key, long id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT id, key, value, is_public, changed_by, at FROM config_history WHERE key = @k AND id = @i");
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("i", id);
        return (await ReadHistoryAsync(cmd, ct)).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<ConfigHistoryEntry>> ReadHistoryAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ConfigHistoryEntry>();
        while (await r.ReadAsync(ct))
        {
            JsonElement? value = r.IsDBNull(2) ? null : JsonDocument.Parse(r.GetString(2)).RootElement.Clone();
            list.Add(new ConfigHistoryEntry(r.GetInt64(0), r.GetString(1), value, r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetDateTime(5)));
        }
        return list;
    }
}

using System.Text.Json;
using GameServer.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

public sealed class PostgresStorage : IStorage
{
    private readonly NpgsqlDataSource _db;

    public PostgresStorage(NpgsqlDataSource db) => _db = db;

    public async Task<T?> ReadAsync<T>(string userId, string key, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT value FROM storage WHERE user_id = @u AND key = @k");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", key);

        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public async Task WriteAsync<T>(string userId, string key, T value, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO storage (user_id, key, value) VALUES (@u, @k, @v)
            ON CONFLICT (user_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(value) });

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

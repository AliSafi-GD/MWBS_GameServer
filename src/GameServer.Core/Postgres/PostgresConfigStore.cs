using System.Text.Json;
using GameServer.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

/// <summary>
/// Config in Postgres with a 5-second in-memory snapshot, so public clients and the maintenance check
/// do not hit the database on every request. Writes on this instance show up immediately; other instances
/// see them within the TTL.
/// </summary>
public sealed class PostgresConfigStore : IConfigStore, IConfig
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _db;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(IReadOnlyList<ConfigEntry> Entries, DateTime LoadedUtc);

    public PostgresConfigStore(NpgsqlDataSource db) => _db = db;

    public async Task<IReadOnlyList<ConfigEntry>> ListAsync(CancellationToken ct = default) =>
        (await LoadAsync(ct)).Entries;

    public async Task<ConfigEntry?> GetAsync(string key, CancellationToken ct = default) =>
        (await LoadAsync(ct)).Entries.FirstOrDefault(e => e.Key == key);

    async Task<T?> IConfig.GetAsync<T>(string key, CancellationToken ct) where T : default
    {
        var entry = await GetAsync(key, ct);
        return entry is null ? default : entry.Value.Deserialize<T>(ReadOptions);
    }

    public async Task<ConfigEntry> SetAsync(string key, JsonElement value, bool isPublic, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO config (key, value, is_public) VALUES (@k, @v, @p)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, is_public = EXCLUDED.is_public, updated_at = now()
            RETURNING value, updated_at
            """);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = value.GetRawText() });
        cmd.Parameters.AddWithValue("p", isPublic);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        using var doc = JsonDocument.Parse(reader.GetString(0));
        var entry = new ConfigEntry(key, doc.RootElement.Clone(), isPublic, reader.GetDateTime(1));

        _snapshot = null; // next read reloads
        return entry;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM config WHERE key = @k");
        cmd.Parameters.AddWithValue("k", key);
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        _snapshot = null;
        return deleted;
    }

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        var snap = _snapshot;
        if (snap is not null && DateTime.UtcNow - snap.LoadedUtc < Ttl)
            return snap;

        await _gate.WaitAsync(ct);
        try
        {
            snap = _snapshot;
            if (snap is not null && DateTime.UtcNow - snap.LoadedUtc < Ttl)
                return snap;

            var entries = new List<ConfigEntry>();
            await using var cmd = _db.CreateCommand("SELECT key, value, is_public, updated_at FROM config ORDER BY key");
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    using var doc = JsonDocument.Parse(reader.GetString(1));
                    entries.Add(new ConfigEntry(reader.GetString(0), doc.RootElement.Clone(), reader.GetBoolean(2), reader.GetDateTime(3)));
                }
            }

            _snapshot = snap = new Snapshot(entries, DateTime.UtcNow);
            return snap;
        }
        finally
        {
            _gate.Release();
        }
    }
}

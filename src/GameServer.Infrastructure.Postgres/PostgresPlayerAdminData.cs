using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core.Admin;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Infrastructure.Postgres;

/// <summary>Admin views of a player: storage records and the account timeline.</summary>
public sealed class PostgresPlayerAdminData : IPlayerAdminData
{
    private readonly NpgsqlDataSource _db;

    public PostgresPlayerAdminData(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<StorageRecord>> ListStorageAsync(string userId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT key, value, updated_at FROM storage WHERE user_id = @u ORDER BY key");
        cmd.Parameters.AddWithValue("u", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<StorageRecord>();
        while (await r.ReadAsync(ct))
            list.Add(new StorageRecord(r.GetString(0), JsonDocument.Parse(r.GetString(1)).RootElement.Clone(), r.GetDateTime(2)));
        return list;
    }

    public async Task WriteStorageAsync(string userId, string key, JsonElement value, CancellationToken ct)
    {
        var raw = value.GetRawText();
        AdminRules.ValidateStorageWrite(key, raw);

        await using var cmd = _db.CreateCommand("""
            INSERT INTO storage (user_id, key, value, updated_at) VALUES (@u, @k, @v, now())
            ON CONFLICT (user_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = raw });
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "No such user.");
        }
    }

    public async Task<bool> DeleteStorageAsync(string userId, string key, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM storage WHERE user_id = @u AND key = @k");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<TimelineEvent>> TimelineAsync(string userId, int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            SELECT at, type, title, detail FROM (
                SELECT created_at AS at, 'ledger' AS type, kind || ' ' || name AS title,
                       CASE WHEN delta > 0 THEN '+' ELSE '' END || delta::text || ' (balance ' || balance_after::text || ') · ' || reason AS detail
                FROM ledger WHERE user_id = @u
                UNION ALL
                SELECT created_at, 'notification', subject,
                       CASE WHEN claimed_at IS NOT NULL THEN 'claimed' WHEN read_at IS NOT NULL THEN 'read' ELSE 'unread' END
                FROM notifications WHERE user_id = @u
                UNION ALL
                SELECT created_at, 'chat', channel, text FROM chat_messages WHERE sender_id = @u
                UNION ALL
                SELECT created_at, 'account', 'created', COALESCE(username, '') FROM users WHERE id = @u
                UNION ALL
                SELECT last_login_at, 'account', 'last login', NULL::text FROM users WHERE id = @u AND last_login_at IS NOT NULL
                UNION ALL
                SELECT at, 'admin', actor, method || ' ' || path FROM admin_audit WHERE path LIKE @p
            ) t
            ORDER BY at DESC
            LIMIT @n
            """);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("p", "/users/" + AdminRules.EscapeLike(userId) + "/%");
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 500));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TimelineEvent>();
        while (await r.ReadAsync(ct))
            list.Add(new TimelineEvent(r.GetDateTime(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }
}

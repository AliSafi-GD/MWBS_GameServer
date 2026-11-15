using GameServer.Abstractions;
using GameServer.Core.Admin;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

/// <summary>The admin audit log in table admin_audit.</summary>
public sealed class PostgresAdminAudit : IAdminAuditLog
{
    private readonly NpgsqlDataSource _db;

    public PostgresAdminAudit(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task AuditAsync(AdminActor actor, string method, string path, string? body, int status, string? ip, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "INSERT INTO admin_audit (actor, role, method, path, body, status, ip) VALUES (@a, @r, @m, @p, @b, @s, @ip)");
        cmd.Parameters.AddWithValue("a", actor.Name);
        cmd.Parameters.AddWithValue("r", actor.Role.ToString());
        cmd.Parameters.AddWithValue("m", method);
        cmd.Parameters.AddWithValue("p", AdminRules.AuditPath(path));
        cmd.Parameters.AddWithValue("b", (object?)AdminRules.RedactBody(body) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("ip", (object?)ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string? actor, string? text, long? before, int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            SELECT id, at, actor, role, method, path, body, status, ip
            FROM admin_audit
            WHERE (@a::text IS NULL OR actor = @a)
              AND (@q::text IS NULL OR path ILIKE @q OR body ILIKE @q)
              AND (@b::bigint IS NULL OR id < @b)
            ORDER BY id DESC
            LIMIT @n
            """);
        cmd.Parameters.AddWithValue("a", (string.IsNullOrWhiteSpace(actor) ? (object)DBNull.Value : actor));
        cmd.Parameters.AddWithValue("q", (string.IsNullOrWhiteSpace(text) ? (object)DBNull.Value : AdminRules.ContainsPattern(text)));
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Bigint) { Value = (object?)before ?? DBNull.Value });
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 500));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditEntry>();
        while (await r.ReadAsync(ct))
            list.Add(new AuditEntry(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM admin_audit WHERE at < now() - interval '180 days'");
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

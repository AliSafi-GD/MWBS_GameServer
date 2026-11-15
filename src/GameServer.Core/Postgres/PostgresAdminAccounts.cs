using GameServer.Abstractions;
using GameServer.Core.Admin;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

/// <summary>Admin accounts in table admin_accounts.</summary>
public sealed class PostgresAdminAccounts : IAdminAccountStore
{
    private readonly NpgsqlDataSource _db;
    private readonly TimeProvider _time;

    public PostgresAdminAccounts(NpgsqlDataSource db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<AdminActor?> AuthenticateAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        await using var cmd = _db.CreateCommand("SELECT id, name, role FROM admin_accounts WHERE key_hash = @h AND NOT disabled");
        cmd.Parameters.AddWithValue("h", AdminKeys.Hash(key));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var actor = new AdminActor(reader.GetString(0), reader.GetString(1),
            AdminPolicy.TryParseRole(reader.GetString(2), out var role) ? role : AdminRole.ReadOnly);
        await reader.CloseAsync();

        await using var touch = _db.CreateCommand(
            "UPDATE admin_accounts SET last_used_at = now() WHERE id = @i AND (last_used_at IS NULL OR last_used_at < now() - interval '1 minute')");
        touch.Parameters.AddWithValue("i", actor.Id);
        await touch.ExecuteNonQueryAsync(ct);
        return actor;
    }

    public async Task<IReadOnlyList<AdminAccount>> ListAdminsAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT id, name, role, created_at, created_by, disabled, last_used_at FROM admin_accounts ORDER BY created_at");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AdminAccount>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadAccount(reader));
        return list;
    }

    private static AdminAccount ReadAccount(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetDateTime(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetDateTime(6));

    /// <summary>Creates an admin and returns the key (only shown this once; only its hash is stored).</summary>
    public async Task<(AdminAccount Account, string Key)> CreateAdminAsync(string name, AdminRole role, string createdBy, CancellationToken ct)
    {
        AdminKeys.ValidateName(name);

        var id = Guid.NewGuid().ToString("N");
        var key = AdminKeys.New();

        try
        {
            await using var cmd = _db.CreateCommand(
                "INSERT INTO admin_accounts (id, name, role, key_hash, created_by) VALUES (@i, @n, @r, @h, @c)");
            cmd.Parameters.AddWithValue("i", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("r", role.ToString());
            cmd.Parameters.AddWithValue("h", AdminKeys.Hash(key));
            cmd.Parameters.AddWithValue("c", createdBy);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GameException("name_taken", "An admin with this name already exists.");
        }

        return (new AdminAccount(id, name, role.ToString(), _time.UtcNow(), createdBy, false, null), key);
    }

    public async Task<bool> UpdateAdminAsync(string id, AdminRole? role, bool? disabled, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE admin_accounts SET role = COALESCE(@r, role), disabled = COALESCE(@d, disabled) WHERE id = @i");
        cmd.Parameters.AddWithValue("i", id);
        cmd.Parameters.AddWithValue("r", (object?)role?.ToString() ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Boolean) { Value = (object?)disabled ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Replaces the key of an admin (for a lost key). Returns the new key, or null if the admin does not exist.</summary>
    public async Task<string?> ResetKeyAsync(string id, CancellationToken ct)
    {
        var key = AdminKeys.New();
        await using var cmd = _db.CreateCommand("UPDATE admin_accounts SET key_hash = @h WHERE id = @i");
        cmd.Parameters.AddWithValue("h", AdminKeys.Hash(key));
        cmd.Parameters.AddWithValue("i", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0 ? key : null;
    }

    public async Task<bool> DeleteAdminAsync(string id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM admin_accounts WHERE id = @i");
        cmd.Parameters.AddWithValue("i", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}

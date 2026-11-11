using System.Text.Json;
using GameServer.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

public sealed class PostgresUserStore : IUserStore
{
    private const string Cols =
        "id, username, display_name, avatar, lang, timezone, created_at, updated_at, last_login_at, disabled";

    private const string ColsU =
        "u.id, u.username, u.display_name, u.avatar, u.lang, u.timezone, u.created_at, u.updated_at, u.last_login_at, u.disabled";

    private readonly NpgsqlDataSource _db;

    public PostgresUserStore(NpgsqlDataSource db) => _db = db;

    // ---- account creation (shared by every login type) ------------------------------------------------------

    /// <summary>
    /// Creates a user and its first login in one transaction. <paramref name="insertLogin"/> adds the login row and
    /// returns false if that login already exists (someone registered it at the same moment); then the existing
    /// account is returned instead. A generated username that collides is simply retried.
    /// </summary>
    private static async Task<User?> CreateAccountAsync(
        NpgsqlConnection conn,
        string? requestedUsername,
        Func<NpgsqlTransaction, string, CancellationToken, Task<bool>> insertLogin,
        Func<CancellationToken, Task<User?>> findExisting,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);

            var id = Guid.NewGuid().ToString("N");
            var username = requestedUsername ?? "player_" + Guid.NewGuid().ToString("N")[..8];

            User? created = null;
            await using (var insertUser = new NpgsqlCommand(
                $"INSERT INTO users (id, username, last_login_at) VALUES (@id, @un, now()) " +
                $"ON CONFLICT (username) DO NOTHING RETURNING {Cols}", conn, tx))
            {
                insertUser.Parameters.AddWithValue("id", id);
                insertUser.Parameters.AddWithValue("un", username);
                await using var reader = await insertUser.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    created = ReadUser(reader);
            }

            if (created is null)
            {
                await tx.RollbackAsync(ct);
                if (requestedUsername is not null)
                    throw new GameException("username_taken", "This username is already taken.");
                continue; // generated name collided: try another
            }

            if (!await insertLogin(tx, created.Id, ct))
            {
                await tx.RollbackAsync(ct);
                return await findExisting(ct);
            }

            await tx.CommitAsync(ct);
            return created;
        }

        throw new InvalidOperationException("Could not generate a unique username.");
    }

    private static async Task TouchLoginAsync(NpgsqlConnection conn, string userId, CancellationToken ct)
    {
        await using var touch = new NpgsqlCommand("UPDATE users SET last_login_at = now() WHERE id = @id", conn);
        touch.Parameters.AddWithValue("id", userId);
        await touch.ExecuteNonQueryAsync(ct);
    }

    // ---- device login ---------------------------------------------------------------------------------------

    public async Task<User?> LoginDeviceAsync(string deviceId, bool create, string? username, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        var existing = await FindByDeviceAsync(conn, deviceId, ct);
        if (existing is not null)
        {
            await TouchLoginAsync(conn, existing.Id, ct);
            return existing;
        }

        if (!create)
            return null;

        return await CreateAccountAsync(
            conn,
            username,
            async (tx, userId, token) =>
            {
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO user_devices (device_id, user_id) VALUES (@d, @u) ON CONFLICT (device_id) DO NOTHING", conn, tx);
                cmd.Parameters.AddWithValue("d", deviceId);
                cmd.Parameters.AddWithValue("u", userId);
                return await cmd.ExecuteNonQueryAsync(token) > 0;
            },
            token => FindByDeviceAsync(conn, deviceId, token),
            ct);
    }

    private static async Task<User?> FindByDeviceAsync(NpgsqlConnection conn, string deviceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {ColsU} FROM user_devices d JOIN users u ON u.id = d.user_id WHERE d.device_id = @d", conn);
        cmd.Parameters.AddWithValue("d", deviceId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    // ---- external identities --------------------------------------------------------------------------------

    public async Task<User?> LoginWithIdentityAsync(string provider, string subject, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        var user = await FindByIdentityAsync(conn, provider, subject, ct);
        if (user is not null)
            await TouchLoginAsync(conn, user.Id, ct);
        return user;
    }

    public async Task<User?> CreateWithIdentityAsync(string provider, string subject, string label, string? username, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        return await CreateAccountAsync(
            conn,
            username,
            async (tx, userId, token) =>
            {
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO user_identities (provider, subject, user_id, label) VALUES (@p, @s, @u, @l) ON CONFLICT DO NOTHING",
                    conn, tx);
                cmd.Parameters.AddWithValue("p", provider);
                cmd.Parameters.AddWithValue("s", subject);
                cmd.Parameters.AddWithValue("u", userId);
                cmd.Parameters.AddWithValue("l", label);
                return await cmd.ExecuteNonQueryAsync(token) > 0;
            },
            token => FindByIdentityAsync(conn, provider, subject, token),
            ct);
    }

    private static async Task<User?> FindByIdentityAsync(NpgsqlConnection conn, string provider, string subject, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT {ColsU}
            FROM user_identities i JOIN users u ON u.id = i.user_id
            WHERE i.provider = @p AND i.subject = @s
            """, conn);
        cmd.Parameters.AddWithValue("p", provider);
        cmd.Parameters.AddWithValue("s", subject);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task<LinkResult> LinkIdentityAsync(string userId, string provider, string subject, string label, CancellationToken ct = default)
    {
        await using (var owner = _db.CreateCommand("SELECT user_id FROM user_identities WHERE provider = @p AND subject = @s"))
        {
            owner.Parameters.AddWithValue("p", provider);
            owner.Parameters.AddWithValue("s", subject);
            var currentOwner = await owner.ExecuteScalarAsync(ct) as string;
            if (currentOwner is not null)
                return currentOwner == userId ? LinkResult.AlreadyLinked : LinkResult.InUseByOtherAccount;
        }

        await using (var has = _db.CreateCommand("SELECT 1 FROM user_identities WHERE user_id = @u AND provider = @p"))
        {
            has.Parameters.AddWithValue("u", userId);
            has.Parameters.AddWithValue("p", provider);
            if (await has.ExecuteScalarAsync(ct) is not null)
                return LinkResult.ProviderAlreadyLinked;
        }

        // ON CONFLICT DO NOTHING also covers a race where another request linked it between the checks above.
        await using var insert = _db.CreateCommand(
            "INSERT INTO user_identities (provider, subject, user_id, label) VALUES (@p, @s, @u, @l) ON CONFLICT DO NOTHING");
        insert.Parameters.AddWithValue("p", provider);
        insert.Parameters.AddWithValue("s", subject);
        insert.Parameters.AddWithValue("u", userId);
        insert.Parameters.AddWithValue("l", label);
        return await insert.ExecuteNonQueryAsync(ct) > 0 ? LinkResult.Linked : LinkResult.InUseByOtherAccount;
    }

    public async Task<UnlinkResult> UnlinkIdentityAsync(string userId, string provider, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the user row so two simultaneous unlinks cannot both pass the "last login method" check.
        await using (var lockUser = new NpgsqlCommand("SELECT 1 FROM users WHERE id = @u FOR UPDATE", conn, tx))
        {
            lockUser.Parameters.AddWithValue("u", userId);
            await lockUser.ExecuteScalarAsync(ct);
        }

        long devices, identities;
        bool hasProvider;
        await using (var count = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM user_devices WHERE user_id = @u),
                   (SELECT count(*) FROM user_identities WHERE user_id = @u),
                   EXISTS (SELECT 1 FROM user_identities WHERE user_id = @u AND provider = @p)
            """, conn, tx))
        {
            count.Parameters.AddWithValue("u", userId);
            count.Parameters.AddWithValue("p", provider);
            await using var reader = await count.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            devices = reader.GetInt64(0);
            identities = reader.GetInt64(1);
            hasProvider = reader.GetBoolean(2);
        }

        if (!hasProvider)
            return UnlinkResult.NotLinked;
        if (devices + identities <= 1)
            return UnlinkResult.LastLoginMethod;

        await using (var delete = new NpgsqlCommand("DELETE FROM user_identities WHERE user_id = @u AND provider = @p", conn, tx))
        {
            delete.Parameters.AddWithValue("u", userId);
            delete.Parameters.AddWithValue("p", provider);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return UnlinkResult.Unlinked;
    }

    public async Task<IReadOnlyList<IdentityInfo>> ListIdentitiesAsync(string userId, CancellationToken ct = default)
    {
        var result = new List<IdentityInfo>();
        await using var cmd = _db.CreateCommand(
            "SELECT provider, label, created_at FROM user_identities WHERE user_id = @u ORDER BY created_at");
        cmd.Parameters.AddWithValue("u", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new IdentityInfo(reader.GetString(0), reader.GetString(1), reader.GetDateTime(2)));
        return result;
    }

    // ---- reads ----------------------------------------------------------------------------------------------

    public async Task<User?> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Cols} FROM users WHERE id = @id");
        cmd.Parameters.AddWithValue("id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task<IReadOnlyList<PublicProfile>> GetPublicAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        var result = new List<PublicProfile>();
        if (userIds.Count == 0)
            return result;

        await using var cmd = _db.CreateCommand(
            "SELECT id, username, display_name, avatar, created_at FROM users WHERE id = ANY(@ids) AND NOT disabled");
        cmd.Parameters.AddWithValue("ids", userIds.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PublicProfile(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4)));
        }
        return result;
    }

    // ---- writes ---------------------------------------------------------------------------------------------

    public async Task<User?> UpdateProfileAsync(string userId, ProfileUpdate update, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            UPDATE users SET
                username     = COALESCE(@un, username),
                display_name = COALESCE(@dn, display_name),
                avatar       = COALESCE(@av, avatar),
                lang         = COALESCE(@lg, lang),
                timezone     = COALESCE(@tz, timezone),
                updated_at   = now()
            WHERE id = @id
            RETURNING {Cols}
            """);
        cmd.Parameters.Add(Text("un", update.Username));
        cmd.Parameters.Add(Text("dn", update.DisplayName));
        cmd.Parameters.Add(Text("av", update.Avatar));
        cmd.Parameters.Add(Text("lg", update.Lang));
        cmd.Parameters.Add(Text("tz", update.Timezone));
        cmd.Parameters.AddWithValue("id", userId);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GameException("username_taken", "This username is already taken.");
        }
    }

    public async Task<bool> SetDisabledAsync(string userId, bool disabled, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE users SET disabled = @d, updated_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("d", disabled);
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteAsync(string userId, CancellationToken ct = default)
    {
        // user_devices, user_identities and storage rows are removed by ON DELETE CASCADE.
        await using var cmd = _db.CreateCommand("DELETE FROM users WHERE id = @id");
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---- admin ----------------------------------------------------------------------------------------------

    public async Task<UserPage> SearchAsync(string? query, int limit, int offset, CancellationToken ct = default)
    {
        string? exact = null;
        string? pattern = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            exact = query.Trim();
            pattern = "%" + exact.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        }

        const string where = "(@pat::text IS NULL OR username ILIKE @pat OR display_name ILIKE @pat OR id = @exact)";

        int total;
        await using (var count = _db.CreateCommand($"SELECT count(*) FROM users WHERE {where}"))
        {
            count.Parameters.Add(Text("pat", pattern));
            count.Parameters.Add(Text("exact", exact));
            total = (int)(long)(await count.ExecuteScalarAsync(ct))!;
        }

        var items = new List<User>();
        await using (var list = _db.CreateCommand(
            $"SELECT {Cols} FROM users WHERE {where} ORDER BY created_at DESC LIMIT @lim OFFSET @off"))
        {
            list.Parameters.Add(Text("pat", pattern));
            list.Parameters.Add(Text("exact", exact));
            list.Parameters.AddWithValue("lim", limit);
            list.Parameters.AddWithValue("off", offset);

            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                items.Add(ReadUser(reader));
        }

        return new UserPage(items, total);
    }

    public async Task<UserExport?> ExportAsync(string userId, CancellationToken ct = default)
    {
        var user = await GetAsync(userId, ct);
        if (user is null)
            return null;

        var devices = new List<DeviceInfo>();
        await using (var cmd = _db.CreateCommand(
            "SELECT device_id, created_at FROM user_devices WHERE user_id = @id ORDER BY created_at"))
        {
            cmd.Parameters.AddWithValue("id", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                devices.Add(new DeviceInfo(Mask(reader.GetString(0)), reader.GetDateTime(1)));
        }

        var identities = await ListIdentitiesAsync(userId, ct);

        var storage = new List<StorageEntry>();
        await using (var cmd = _db.CreateCommand(
            "SELECT key, value, updated_at FROM storage WHERE user_id = @id ORDER BY key"))
        {
            cmd.Parameters.AddWithValue("id", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                storage.Add(new StorageEntry(reader.GetString(0), doc.RootElement.Clone(), reader.GetDateTime(2)));
            }
        }

        return new UserExport(user, devices, identities, storage);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static User ReadUser(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetDateTime(6), r.GetDateTime(7),
        r.IsDBNull(8) ? null : r.GetDateTime(8),
        r.GetBoolean(9));

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static string Mask(string deviceId) =>
        deviceId.Length <= 8 ? new string('*', deviceId.Length) : deviceId[..4] + "…" + deviceId[^4..];
}

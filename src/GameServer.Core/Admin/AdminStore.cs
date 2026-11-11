using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Admin;

public enum AdminRole { ReadOnly = 0, Support = 1, Admin = 2 }

/// <summary>Who is calling the admin API. The master key from settings is "root".</summary>
public sealed record AdminActor(string Id, string Name, AdminRole Role);

public sealed record AdminAccount(string Id, string Name, string Role, DateTime CreatedUtc, string? CreatedBy, bool Disabled, DateTime? LastUsedUtc);

public sealed record AuditEntry(long Id, DateTime AtUtc, string Actor, string Role, string Method, string Path, string? Body, int Status, string? Ip);

public sealed record ConfigHistoryEntry(long Id, string Key, JsonElement? Value, bool IsPublic, string? ChangedBy, DateTime AtUtc);

public sealed record ScheduledBroadcast(
    long Id, DateTime RunUtc, string Subject, string? Body, NotificationReward? Reward, string Audience,
    int? ExpiresInDays, string Status, string? CreatedBy, DateTime CreatedUtc, DateTime? SentUtc, int? SentCount, string? Error);

public sealed record MetricPoint(DateTime AtUtc, int Online, int Matches, int Queued);

public sealed record StorageRecord(string Key, JsonElement Value, DateTime UpdatedUtc);

public sealed record TimelineEvent(DateTime AtUtc, string Type, string Title, string? Detail);

public sealed record DayCount(string Day, int Count);

public sealed record EconomyFlow(string Name, long Earned, long Spent);

public sealed record DashboardTotals(
    int Users, int Banned, int Signups24h, int Logins24h, int Signups7d, IReadOnlyList<EconomyFlow> Economy24h,
    IReadOnlyList<DayCount> SignupsPerDay);

/// <summary>Which role may call which admin route. Paths are relative to /admin/api.</summary>
public static class AdminPolicy
{
    private static readonly Regex[] SupportWrites =
    {
        new(@"^/users/[^/]+/(ban|unban|mute|disconnect|notifications)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^/chat/messages/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^/groups/[^/]+/members/[^/]+/(kick|ban)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    public static bool IsAllowed(AdminRole role, string method, string path)
    {
        if (role == AdminRole.Admin)
            return true;

        var read = method is "GET" or "HEAD";
        if (read)
            return !(path.StartsWith("/admins", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/audit", StringComparison.OrdinalIgnoreCase));

        if (role != AdminRole.Support)
            return false;

        if (method is not ("POST" or "DELETE"))
            return false;

        return SupportWrites.Any(r => r.IsMatch(path));
    }

    public static bool TryParseRole(string? text, out AdminRole role) =>
        Enum.TryParse(text, ignoreCase: true, out role) && Enum.IsDefined(role);
}

/// <summary>Admin accounts, audit log, config history, scheduled broadcasts, dashboard numbers and per-player admin views.</summary>
public sealed class AdminStore
{
    private static readonly Regex Secret = new("(\"[^\"]*(?:key|secret|password|token|credential)[^\"]*\"\\s*:\\s*)\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly NpgsqlDataSource _db;

    public AdminStore(NpgsqlDataSource db) => _db = db;

    // ---- accounts ----

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<AdminActor?> AuthenticateAsync(string given, string masterKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(given))
            return null;

        if (!string.IsNullOrEmpty(masterKey)
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(masterKey))))
            return new AdminActor("root", "root", AdminRole.Admin);

        await using var cmd = _db.CreateCommand("SELECT id, name, role FROM admin_accounts WHERE key_hash = @h AND NOT disabled");
        cmd.Parameters.AddWithValue("h", HashKey(given));
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
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]{2,32}$") || name.Equals("root", StringComparison.OrdinalIgnoreCase))
            throw new GameException("invalid_name", "Name must be 2-32 letters, digits, '_', '.' or '-' (and not 'root').");

        var id = Guid.NewGuid().ToString("N");
        var key = "gsk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        try
        {
            await using var cmd = _db.CreateCommand(
                "INSERT INTO admin_accounts (id, name, role, key_hash, created_by) VALUES (@i, @n, @r, @h, @c)");
            cmd.Parameters.AddWithValue("i", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("r", role.ToString());
            cmd.Parameters.AddWithValue("h", HashKey(key));
            cmd.Parameters.AddWithValue("c", createdBy);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GameException("name_taken", "An admin with this name already exists.");
        }

        return (new AdminAccount(id, name, role.ToString(), DateTime.UtcNow, createdBy, false, null), key);
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
        var key = "gsk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var cmd = _db.CreateCommand("UPDATE admin_accounts SET key_hash = @h WHERE id = @i");
        cmd.Parameters.AddWithValue("h", HashKey(key));
        cmd.Parameters.AddWithValue("i", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0 ? key : null;
    }

    public async Task<bool> DeleteAdminAsync(string id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM admin_accounts WHERE id = @i");
        cmd.Parameters.AddWithValue("i", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---- audit ----

    public static string? RedactBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var text = Secret.Replace(body, "$1\"***\"");
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }

    public async Task AuditAsync(AdminActor actor, string method, string path, string? body, int status, string? ip, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "INSERT INTO admin_audit (actor, role, method, path, body, status, ip) VALUES (@a, @r, @m, @p, @b, @s, @ip)");
        cmd.Parameters.AddWithValue("a", actor.Name);
        cmd.Parameters.AddWithValue("r", actor.Role.ToString());
        cmd.Parameters.AddWithValue("m", method);
        cmd.Parameters.AddWithValue("p", path.Length > 500 ? path[..500] : path);
        cmd.Parameters.AddWithValue("b", (object?)RedactBody(body) ?? DBNull.Value);
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
        cmd.Parameters.AddWithValue("q", (string.IsNullOrWhiteSpace(text) ? (object)DBNull.Value : "%" + text.Replace("%", "\\%").Replace("_", "\\_") + "%"));
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Bigint) { Value = (object?)before ?? DBNull.Value });
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 500));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditEntry>();
        while (await r.ReadAsync(ct))
            list.Add(new AuditEntry(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    // ---- config history ----

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

    // ---- player storage (admin view: list / write / delete) ----

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
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new GameException("invalid_key", "Storage key must be 1-128 characters.");
        var raw = value.GetRawText();
        if (raw.Length > 256 * 1024)
            throw new GameException("too_large", "Storage value is limited to 256 KB.");

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

    // ---- player timeline ----

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
        cmd.Parameters.AddWithValue("p", "/users/" + userId.Replace("%", "\\%").Replace("_", "\\_") + "/%");
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 500));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TimelineEvent>();
        while (await r.ReadAsync(ct))
            list.Add(new TimelineEvent(r.GetDateTime(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    // ---- scheduled broadcasts (live-ops) ----

    public async Task<ScheduledBroadcast> ScheduleAsync(
        DateTime runUtc, string subject, string? body, NotificationReward? reward, string audience, int? expiresInDays,
        string createdBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
            throw new GameException("invalid_subject", "Subject is required (max 200 characters).");
        if (audience is not ("all" or "online"))
            throw new GameException("invalid_audience", "Audience must be 'all' or 'online'.");

        await using var cmd = _db.CreateCommand("""
            INSERT INTO scheduled_broadcasts (run_at, subject, body, reward, audience, expires_in_days, created_by)
            VALUES (@t, @s, @b, @r, @a, @e, @c)
            RETURNING id
            """);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(runUtc, DateTimeKind.Utc) });
        cmd.Parameters.AddWithValue("s", subject.Trim());
        cmd.Parameters.AddWithValue("b", (object?)body ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb)
            { Value = reward is null ? (object)DBNull.Value : JsonSerializer.Serialize(reward) });
        cmd.Parameters.AddWithValue("a", audience);
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Integer) { Value = (object?)expiresInDays ?? DBNull.Value });
        cmd.Parameters.AddWithValue("c", createdBy);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return (await GetBroadcastAsync(id, ct))!;
    }

    private const string BroadcastColumns =
        "id, run_at, subject, body, reward, audience, expires_in_days, status, created_by, created_at, sent_at, sent_count, error";

    public async Task<ScheduledBroadcast?> GetBroadcastAsync(long id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT {BroadcastColumns} FROM scheduled_broadcasts WHERE id = @i");
        cmd.Parameters.AddWithValue("i", id);
        return (await ReadBroadcastsAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ScheduledBroadcast>> ListBroadcastsAsync(int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT {BroadcastColumns} FROM scheduled_broadcasts ORDER BY run_at DESC LIMIT @n");
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 200));
        return await ReadBroadcastsAsync(cmd, ct);
    }

    /// <summary>Claims due broadcasts (so two server instances never send the same one twice).</summary>
    public async Task<IReadOnlyList<ScheduledBroadcast>> ClaimDueAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"""
            UPDATE scheduled_broadcasts SET status = 'sending'
            WHERE id IN (SELECT id FROM scheduled_broadcasts WHERE status = 'pending' AND run_at <= now()
                         ORDER BY run_at LIMIT 10 FOR UPDATE SKIP LOCKED)
            RETURNING {BroadcastColumns}
            """);
        return await ReadBroadcastsAsync(cmd, ct);
    }

    public async Task FinishBroadcastAsync(long id, int sent, string? error, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE scheduled_broadcasts SET status = @s, sent_at = now(), sent_count = @c, error = @e WHERE id = @i");
        cmd.Parameters.AddWithValue("s", error is null ? "sent" : "failed");
        cmd.Parameters.AddWithValue("c", sent);
        cmd.Parameters.AddWithValue("e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Cancels a pending broadcast, or removes a finished one. Returns false if it does not exist or is being sent.</summary>
    public async Task<bool> DeleteBroadcastAsync(long id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM scheduled_broadcasts WHERE id = @i AND status <> 'sending'");
        cmd.Parameters.AddWithValue("i", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<IReadOnlyList<ScheduledBroadcast>> ReadBroadcastsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ScheduledBroadcast>();
        while (await r.ReadAsync(ct))
        {
            NotificationReward? reward = r.IsDBNull(4)
                ? null
                : JsonSerializer.Deserialize<NotificationReward>(r.GetString(4), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            list.Add(new ScheduledBroadcast(
                r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), reward, r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetDateTime(9),
                r.IsDBNull(10) ? null : r.GetDateTime(10), r.IsDBNull(11) ? null : r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12)));
        }
        return list;
    }

    // ---- dashboard ----

    public async Task SaveSampleAsync(int online, int matches, int queued, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO metrics_samples (at, online, matches, queued) VALUES (date_trunc('minute', now()), @o, @m, @q)
            ON CONFLICT (at) DO UPDATE SET online = GREATEST(metrics_samples.online, EXCLUDED.online),
                                           matches = EXCLUDED.matches, queued = EXCLUDED.queued
            """);
        cmd.Parameters.AddWithValue("o", online);
        cmd.Parameters.AddWithValue("m", matches);
        cmd.Parameters.AddWithValue("q", queued);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            DELETE FROM metrics_samples WHERE at < now() - interval '7 days';
            DELETE FROM admin_audit WHERE at < now() - interval '180 days';
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Samples of the last <paramref name="minutes"/> minutes, averaged into at most ~120 buckets.</summary>
    public async Task<IReadOnlyList<MetricPoint>> SeriesAsync(int minutes, CancellationToken ct)
    {
        minutes = Math.Clamp(minutes, 5, 7 * 24 * 60);
        var bucket = Math.Max(1, minutes / 120);
        await using var cmd = _db.CreateCommand("""
            SELECT to_timestamp(floor(extract(epoch FROM at) / (@b * 60)) * (@b * 60)) AS t,
                   max(online)::int, max(matches)::int, max(queued)::int
            FROM metrics_samples
            WHERE at > now() - make_interval(mins => @m)
            GROUP BY 1 ORDER BY 1
            """);
        cmd.Parameters.AddWithValue("b", bucket);
        cmd.Parameters.AddWithValue("m", minutes);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<MetricPoint>();
        while (await r.ReadAsync(ct))
            list.Add(new MetricPoint(r.GetDateTime(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    public async Task<DashboardTotals> TotalsAsync(CancellationToken ct)
    {
        int users, banned, s24, l24, s7;
        await using (var cmd = _db.CreateCommand("""
            SELECT count(*)::int,
                   count(*) FILTER (WHERE disabled)::int,
                   count(*) FILTER (WHERE created_at > now() - interval '24 hours')::int,
                   count(*) FILTER (WHERE last_login_at > now() - interval '24 hours')::int,
                   count(*) FILTER (WHERE created_at > now() - interval '7 days')::int
            FROM users
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (users, banned, s24, l24, s7) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }

        var economy = new List<EconomyFlow>();
        await using (var cmd = _db.CreateCommand("""
            SELECT name,
                   COALESCE(sum(delta) FILTER (WHERE delta > 0), 0)::bigint,
                   COALESCE(-sum(delta) FILTER (WHERE delta < 0), 0)::bigint
            FROM ledger
            WHERE kind = 'wallet' AND created_at > now() - interval '24 hours'
            GROUP BY name ORDER BY name
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                economy.Add(new EconomyFlow(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));

        var perDay = new List<DayCount>();
        await using (var cmd = _db.CreateCommand("""
            SELECT to_char(d::date, 'YYYY-MM-DD'), COALESCE(c.n, 0)::int
            FROM generate_series(current_date - 6, current_date, interval '1 day') d
            LEFT JOIN (SELECT created_at::date AS day, count(*) AS n FROM users
                       WHERE created_at >= current_date - 6 GROUP BY 1) c ON c.day = d::date
            ORDER BY d
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                perDay.Add(new DayCount(r.GetString(0), r.GetInt32(1)));

        return new DashboardTotals(users, banned, s24, l24, s7, economy, perDay);
    }
}

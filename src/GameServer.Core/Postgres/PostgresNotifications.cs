using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Abstractions;
using GameServer.Core.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

public sealed class PostgresNotifications : INotificationStore
{
    private const long MaxAmount = 1_000_000_000_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NpgsqlDataSource _db;
    private readonly IEconomy _economy;
    private readonly IConfigStore _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<PostgresNotifications> _log;

    // The realtime layer is looked up on first use: it depends on services that may depend on this store.
    public PostgresNotifications(
        NpgsqlDataSource db, IEconomy economy, IConfigStore config, IServiceProvider services, ILogger<PostgresNotifications> log)
    {
        _db = db;
        _economy = economy;
        _config = config;
        _services = services;
        _log = log;
    }

    /// <summary>
    /// Tells online players there is something new in their inbox: the realtime event <c>notification.new</c>.
    /// The inbox in the database stays the source of truth; players who are offline simply read it later.
    /// </summary>
    private async Task PushAsync(string? userId, long? id, string subject, bool hasReward)
    {
        try
        {
            var realtime = _services.GetService<ConnectionManager>();
            if (realtime is null)
                return;

            var frame = Frames.Build("notification.new", null, new { id, broadcast = userId is null, subject, hasReward });
            if (userId is null)
                await realtime.SendToAllAsync(frame);
            else
                await realtime.SendToUserAsync(userId, frame);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pushing a notification event failed; the notification itself is saved.");
        }
    }

    public async Task<long> SendAsync(
        string userId, string subject, string? body = null, NotificationReward? reward = null,
        DateTime? expiresUtc = null, CancellationToken ct = default)
    {
        var (s, b, r) = await ValidateAsync(subject, body, reward, ct);

        await using var cmd = _db.CreateCommand("""
            INSERT INTO notifications (user_id, subject, body, reward, expires_at)
            VALUES (@u, @s, @b, @r, @e) RETURNING id
            """);
        cmd.Parameters.AddWithValue("u", userId);
        AddContent(cmd, s, b, r, expiresUtc);

        try
        {
            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            await PushAsync(userId, id, s, r is not null);
            return id;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }
    }

    public async Task<int> BroadcastAsync(string subject, string? body, NotificationReward? reward, DateTime? expiresUtc, CancellationToken ct = default)
    {
        var (s, b, r) = await ValidateAsync(subject, body, reward, ct);

        await using var cmd = _db.CreateCommand("""
            INSERT INTO notifications (user_id, subject, body, reward, expires_at)
            SELECT id, @s, @b, @r, @e FROM users WHERE NOT disabled
            """);
        AddContent(cmd, s, b, r, expiresUtc);
        var count = await cmd.ExecuteNonQueryAsync(ct);
        await PushAsync(null, null, s, r is not null);
        return count;
    }

    public async Task<IReadOnlyList<NotificationInfo>> ListAsync(
        string userId, int limit, bool unreadOnly, bool includeExpired = false, CancellationToken ct = default)
    {
        // Housekeeping: drop notifications that expired a month ago, and read ones (reward claimed or none) after 90 days.
        await using (var clean = _db.CreateCommand("""
            DELETE FROM notifications
            WHERE user_id = @u
              AND ((expires_at IS NOT NULL AND expires_at < now() - interval '30 days')
                OR (read_at IS NOT NULL AND created_at < now() - interval '90 days' AND (reward IS NULL OR claimed_at IS NOT NULL)))
            """))
        {
            clean.Parameters.AddWithValue("u", userId);
            await clean.ExecuteNonQueryAsync(ct);
        }

        var result = new List<NotificationInfo>();
        await using var cmd = _db.CreateCommand("""
            SELECT id, subject, body, reward, created_at, expires_at, read_at IS NOT NULL, claimed_at IS NOT NULL
            FROM notifications
            WHERE user_id = @u
              AND (@all OR expires_at IS NULL OR expires_at > now())
              AND (NOT @unread OR read_at IS NULL)
            ORDER BY id DESC
            LIMIT @lim
            """);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("all", includeExpired);
        cmd.Parameters.AddWithValue("unread", unreadOnly);
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 100));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new NotificationInfo(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<NotificationReward>(reader.GetString(3), Json),
                reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.GetBoolean(6), reader.GetBoolean(7)));
        }
        return result;
    }

    public async Task<bool> MarkReadAsync(string userId, long id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE notifications SET read_at = COALESCE(read_at, now()) WHERE id = @id AND user_id = @u");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<NotificationClaim> ClaimAsync(string userId, long id, CancellationToken ct = default)
    {
        string? rewardJson;
        bool claimed;
        DateTime? expires;

        await using (var cmd = _db.CreateCommand(
            "SELECT reward, claimed_at IS NOT NULL, expires_at FROM notifications WHERE id = @id AND user_id = @u"))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new GameException("not_found", "Notification not found.");
            rewardJson = reader.IsDBNull(0) ? null : reader.GetString(0);
            claimed = reader.GetBoolean(1);
            expires = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        }

        if (rewardJson is null)
            throw new GameException("no_reward", "This notification has no reward.");
        if (claimed)
            throw new GameException("already_claimed", "The reward was already claimed.");
        if (expires is { } e && e <= DateTime.UtcNow)
            throw new GameException("expired", "This notification has expired.");

        var reward = JsonSerializer.Deserialize<NotificationReward>(rewardJson, Json)!;

        // The idempotency key makes a double tap (or a crash before the mark below) pay only once.
        var tx = new EconomyTransaction($"notification:{id}", $"notif:{id}");
        foreach (var (name, amount) in reward.Wallet ?? new()) tx.Currency(name, amount);
        foreach (var (name, amount) in reward.Items ?? new()) tx.Item(name, amount);
        var result = await _economy.ApplyAsync(userId, tx, ct);

        await using var mark = _db.CreateCommand("""
            UPDATE notifications SET claimed_at = COALESCE(claimed_at, now()), read_at = COALESCE(read_at, now())
            WHERE id = @id AND user_id = @u
            """);
        mark.Parameters.AddWithValue("id", id);
        mark.Parameters.AddWithValue("u", userId);
        await mark.ExecuteNonQueryAsync(ct);

        return new NotificationClaim(reward, result.State);
    }

    public async Task<bool> DeleteAsync(string userId, long id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            DELETE FROM notifications
            WHERE id = @id AND user_id = @u AND (reward IS NULL OR claimed_at IS NOT NULL OR (expires_at IS NOT NULL AND expires_at <= now()))
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("u", userId);
        if (await cmd.ExecuteNonQueryAsync(ct) > 0)
            return true;

        await using var exists = _db.CreateCommand("SELECT 1 FROM notifications WHERE id = @id AND user_id = @u");
        exists.Parameters.AddWithValue("id", id);
        exists.Parameters.AddWithValue("u", userId);
        if (await exists.ExecuteScalarAsync(ct) is not null)
            throw new GameException("reward_unclaimed", "Claim the reward before deleting this notification.");
        return false;
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private async Task<(string Subject, string? Body, string? RewardJson)> ValidateAsync(
        string subject, string? body, NotificationReward? reward, CancellationToken ct)
    {
        subject = (subject ?? "").Trim();
        if (subject.Length is 0 or > 200)
            throw new GameException("invalid_notification", "The subject is required (at most 200 characters).");
        if (body is { Length: > 4000 })
            throw new GameException("invalid_notification", "The body can be at most 4000 characters.");

        string? rewardJson = null;
        if (reward is not null)
        {
            var wallet = Clean(reward.Wallet);
            var items = Clean(reward.Items);
            if (wallet.Count == 0 && items.Count == 0)
                throw new GameException("invalid_notification", "A reward needs at least one positive wallet or items amount.");

            // A reward that can never be paid out (unknown currency/item) is caught now, not when the player taps claim.
            EconomyCatalog.From(await _config.GetAsync(ConfigRules.Economy, ct))?.Check(wallet.Keys, items.Keys);

            rewardJson = JsonSerializer.Serialize(
                new NotificationReward { Wallet = wallet.Count > 0 ? wallet : null, Items = items.Count > 0 ? items : null }, Json);
        }

        return (subject, string.IsNullOrWhiteSpace(body) ? null : body, rewardJson);
    }

    private static Dictionary<string, long> Clean(Dictionary<string, long>? input)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, amount) in input ?? new())
        {
            if (!ConfigRules.IsValidName(name))
                throw new GameException("invalid_notification", $"Invalid name '{name}' in the reward.");
            if (amount <= 0 || amount > MaxAmount)
                throw new GameException("invalid_notification", $"The reward amount of '{name}' must be a positive whole number.");
            result[name] = amount;
        }
        return result;
    }

    private static void AddContent(NpgsqlCommand cmd, string subject, string? body, string? rewardJson, DateTime? expiresUtc)
    {
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Text) { Value = (object?)body ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = (object?)rewardJson ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.TimestampTz)
        {
            Value = expiresUtc is { } e ? (object)DateTime.SpecifyKind(e, DateTimeKind.Utc) : DBNull.Value
        });
    }
}

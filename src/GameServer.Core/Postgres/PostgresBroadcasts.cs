using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core.Admin;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

/// <summary>Scheduled broadcasts in table scheduled_broadcasts.</summary>
public sealed class PostgresBroadcasts : IBroadcastStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresBroadcasts(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task<ScheduledBroadcast> ScheduleAsync(
        DateTime runUtc, string subject, string? body, NotificationReward? reward, string audience, int? expiresInDays,
        string createdBy, CancellationToken ct)
    {
        AdminRules.ValidateBroadcast(subject, audience);

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
}

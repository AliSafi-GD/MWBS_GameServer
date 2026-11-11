using GameServer.Core.Realtime;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Core.Postgres;

public sealed class PostgresChat : IChatStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresChat(NpgsqlDataSource db) => _db = db;

    public async Task<ChatRow> AddAsync(string channel, string? senderId, string text, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "INSERT INTO chat_messages (channel, sender_id, text) VALUES (@c, @s, @t) RETURNING id, created_at");
        cmd.Parameters.AddWithValue("c", channel);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)senderId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", text);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ChatRow(reader.GetInt64(0), senderId, text, reader.GetDateTime(1));
    }

    public async Task<IReadOnlyList<ChatRow>> ListAsync(string channel, int limit, long? beforeId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT id, sender_id, text, created_at FROM chat_messages " +
            "WHERE channel = @c AND (@b IS NULL OR id < @b) ORDER BY id DESC LIMIT @lim");
        cmd.Parameters.AddWithValue("c", channel);
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Bigint) { Value = (object?)beforeId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 100));

        var result = new List<ChatRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ChatRow(
                reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        }

        return result;
    }

    public async Task DeleteOlderThanAsync(string channel, int days, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM chat_messages WHERE channel = @c AND created_at < now() - make_interval(days => @d)");
        cmd.Parameters.AddWithValue("c", channel);
        cmd.Parameters.AddWithValue("d", days);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteOlderThanAsync(int days, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM chat_messages WHERE created_at < now() - make_interval(days => @d)");
        cmd.Parameters.AddWithValue("d", days);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetMuteAsync(string userId, DateTime? untilUtc, string? reason, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "INSERT INTO chat_mutes (user_id, until_utc, reason) VALUES (@u, @until, @r) " +
            "ON CONFLICT (user_id) DO UPDATE SET until_utc = @until, reason = @r, created_at = now()");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = (object?)untilUtc ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ClearMuteAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM chat_mutes WHERE user_id = @u");
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<MuteInfo?> GetMuteAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT user_id, until_utc, reason, created_at FROM chat_mutes " +
            "WHERE user_id = @u AND (until_utc IS NULL OR until_utc > now())");
        cmd.Parameters.AddWithValue("u", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMute(reader) : null;
    }

    public async Task<IReadOnlyList<MuteInfo>> ListMutesAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT user_id, until_utc, reason, created_at FROM chat_mutes " +
            "WHERE until_utc IS NULL OR until_utc > now() ORDER BY created_at DESC LIMIT @lim");
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 500));

        var result = new List<MuteInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadMute(reader));
        return result;
    }

    public async Task<string?> DeleteMessageAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM chat_messages WHERE id = @id RETURNING channel");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT channel, count(*), max(created_at) FROM chat_messages " +
            "GROUP BY channel ORDER BY max(created_at) DESC LIMIT @lim");
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 200));

        var result = new List<ChannelInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ChannelInfo(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2)));
        return result;
    }

    private static MuteInfo ReadMute(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetDateTime(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetDateTime(3));
}

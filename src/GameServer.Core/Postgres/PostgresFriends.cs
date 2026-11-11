using GameServer.Abstractions;
using GameServer.Core.Social;
using Npgsql;

namespace GameServer.Core.Postgres;

/// <summary>
/// Friendships are stored as two rows per pair, one from each side's point of view:
/// (A,B,invite_sent)+(B,A,invite_received) become (A,B,friend)+(B,A,friend). A block exists only on the blocker's row.
/// Every change locks the pair first so two players adding each other at the same moment cannot deadlock.
/// </summary>
public sealed class PostgresFriends : IFriendStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresFriends(NpgsqlDataSource db) => _db = db;

    public async Task<(FriendState State, bool Changed)> AddAsync(
        string userId, string targetId, int maxFriends, int maxPendingSent, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockPairAsync(conn, tx, userId, targetId, ct);

        var mine = await GetStateAsync(conn, tx, userId, targetId, ct);
        var theirs = await GetStateAsync(conn, tx, targetId, userId, ct);

        if (mine == FriendState.Blocked)
            throw new GameException("blocked", "You blocked this player. Unblock them first.");
        if (theirs == FriendState.Blocked)
            return (FriendState.InviteSent, false); // do not reveal that they blocked the caller
        if (mine == FriendState.Friend)
            return (FriendState.Friend, false);
        if (mine == FriendState.InviteSent)
            return (FriendState.InviteSent, false);

        if (mine == FriendState.InviteReceived)
        {
            // They asked first, so adding them back is accepting.
            if (await CountAsync(conn, tx, userId, FriendState.Friend, ct) >= maxFriends
                || await CountAsync(conn, tx, targetId, FriendState.Friend, ct) >= maxFriends)
                throw new GameException("friend_limit", "The friend list is full.");

            await SetAsync(conn, tx, userId, targetId, FriendState.Friend, ct);
            await SetAsync(conn, tx, targetId, userId, FriendState.Friend, ct);
            await tx.CommitAsync(ct);
            return (FriendState.Friend, true);
        }

        if (await CountAsync(conn, tx, userId, FriendState.Friend, ct) >= maxFriends)
            throw new GameException("friend_limit", "Your friend list is full.");
        if (await CountAsync(conn, tx, userId, FriendState.InviteSent, ct) >= maxPendingSent)
            throw new GameException("too_many_invites", "You have too many friend invites waiting for an answer.");

        await SetAsync(conn, tx, userId, targetId, FriendState.InviteSent, ct);
        await SetAsync(conn, tx, targetId, userId, FriendState.InviteReceived, ct);
        await tx.CommitAsync(ct);
        return (FriendState.InviteSent, true);
    }

    public async Task<bool> RemoveAsync(string userId, string targetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockPairAsync(conn, tx, userId, targetId, ct);

        var removed = await DeleteRowAsync(conn, tx, userId, targetId, blockedToo: false, ct);
        await DeleteRowAsync(conn, tx, targetId, userId, blockedToo: false, ct);

        await tx.CommitAsync(ct);
        return removed;
    }

    public async Task BlockAsync(string userId, string targetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockPairAsync(conn, tx, userId, targetId, ct);

        await SetAsync(conn, tx, userId, targetId, FriendState.Blocked, ct);
        await DeleteRowAsync(conn, tx, targetId, userId, blockedToo: false, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<bool> UnblockAsync(string userId, string targetId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM friendships WHERE user_id = @u AND friend_id = @f AND state = 3");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("f", targetId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<FriendEntry>> ListAsync(
        string userId, FriendState? state, int limit, CancellationToken ct = default)
    {
        var sql = "SELECT u.id, u.username, u.display_name, u.avatar, u.created_at, f.state, f.updated_at " +
                  "FROM friendships f JOIN users u ON u.id = f.friend_id " +
                  "WHERE f.user_id = @u AND NOT u.disabled" +
                  (state is null ? "" : " AND f.state = @s") +
                  " ORDER BY f.updated_at DESC LIMIT @lim";

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("u", userId);
        if (state is not null)
            cmd.Parameters.AddWithValue("s", (short)state.Value);
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 500));

        var result = new List<FriendEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var profile = new PublicProfile(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
            result.Add(new FriendEntry(profile, (FriendState)reader.GetInt16(5), reader.GetDateTime(6)));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> FriendIdsAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT friend_id FROM friendships WHERE user_id = @u AND state = 0");
        cmd.Parameters.AddWithValue("u", userId);

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<bool> AreFriendsAsync(string userId, string otherId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT 1 FROM friendships WHERE user_id = @a AND friend_id = @b AND state = 0");
        cmd.Parameters.AddWithValue("a", userId);
        cmd.Parameters.AddWithValue("b", otherId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> IsBlockedBetweenAsync(string userId, string otherId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT 1 FROM friendships WHERE state = 3 AND " +
            "((user_id = @a AND friend_id = @b) OR (user_id = @b AND friend_id = @a))");
        cmd.Parameters.AddWithValue("a", userId);
        cmd.Parameters.AddWithValue("b", otherId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<string?> FindUserIdByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT id FROM users WHERE username = @n AND NOT disabled");
        cmd.Parameters.AddWithValue("n", username);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static async Task LockPairAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string a, string b, CancellationToken ct)
    {
        var (first, second) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx);
        cmd.Parameters.AddWithValue("k", first + "|" + second);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<FriendState?> GetStateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, string friendId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT state FROM friendships WHERE user_id = @u AND friend_id = @f", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("f", friendId);
        return await cmd.ExecuteScalarAsync(ct) is short state ? (FriendState)state : null;
    }

    private static async Task SetAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, string friendId, FriendState state, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO friendships (user_id, friend_id, state) VALUES (@u, @f, @s) " +
            "ON CONFLICT (user_id, friend_id) DO UPDATE SET state = @s, updated_at = now()", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("f", friendId);
        cmd.Parameters.AddWithValue("s", (short)state);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> DeleteRowAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, string friendId, bool blockedToo, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM friendships WHERE user_id = @u AND friend_id = @f" + (blockedToo ? "" : " AND state <> 3"), conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("f", friendId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, FriendState state, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM friendships WHERE user_id = @u AND state = @s", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("s", (short)state);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}

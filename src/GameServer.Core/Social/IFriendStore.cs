namespace GameServer.Core.Social;

public interface IFriendStore
{
    /// <summary>
    /// Sends a friend invite, or accepts one the other player already sent. Returns the resulting state from the
    /// caller's side and whether anything changed. If the other player blocked the caller nothing is stored and the
    /// answer looks like a normal sent invite. Errors: blocked (caller blocked them), friend_limit, too_many_invites.
    /// </summary>
    Task<(FriendState State, bool Changed)> AddAsync(
        string userId, string targetId, int maxFriends, int maxPendingSent, CancellationToken ct = default);

    /// <summary>Removes a friend, cancels a sent invite or declines a received one. A block is not touched.</summary>
    Task<bool> RemoveAsync(string userId, string targetId, CancellationToken ct = default);

    /// <summary>Blocks the player and drops any friendship or invite between them.</summary>
    Task BlockAsync(string userId, string targetId, CancellationToken ct = default);

    Task<bool> UnblockAsync(string userId, string targetId, CancellationToken ct = default);

    Task<IReadOnlyList<FriendEntry>> ListAsync(string userId, FriendState? state, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<string>> FriendIdsAsync(string userId, CancellationToken ct = default);

    Task<bool> AreFriendsAsync(string userId, string otherId, CancellationToken ct = default);

    /// <summary>True if either of the two blocked the other.</summary>
    Task<bool> IsBlockedBetweenAsync(string userId, string otherId, CancellationToken ct = default);

    /// <summary>Finds an active account by (lower-case) username, or null.</summary>
    Task<string?> FindUserIdByUsernameAsync(string username, CancellationToken ct = default);
}

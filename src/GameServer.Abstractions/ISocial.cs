namespace GameServer.Abstractions;

/// <summary>Read-only view of friends, groups and who is online, for modules and hooks.</summary>
public interface ISocial
{
    /// <summary>True when the player has at least one open realtime connection on this server instance.</summary>
    bool IsOnline(string userId);

    Task<bool> AreFriendsAsync(string userId, string otherUserId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetFriendIdsAsync(string userId, CancellationToken ct = default);

    /// <summary>Owner, admins and members (not pending requests, invites or banned players).</summary>
    Task<IReadOnlyList<string>> GetGroupMemberIdsAsync(string groupId, CancellationToken ct = default);
}

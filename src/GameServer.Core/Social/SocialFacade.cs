using GameServer.Abstractions;
using GameServer.Core.Realtime;

namespace GameServer.Core.Social;

/// <summary>The read-only social view that modules and hooks get through <see cref="ISocial"/>.</summary>
public sealed class SocialFacade : ISocial
{
    private readonly IFriendStore _friends;
    private readonly IGroupStore _groups;
    private readonly IRealtime _manager;

    public SocialFacade(IFriendStore friends, IGroupStore groups, IRealtime manager)
    {
        _friends = friends;
        _groups = groups;
        _manager = manager;
    }

    public bool IsOnline(string userId) => _manager.IsOnline(userId);

    public Task<bool> AreFriendsAsync(string userId, string otherUserId, CancellationToken ct = default) =>
        _friends.AreFriendsAsync(userId, otherUserId, ct);

    public Task<IReadOnlyList<string>> GetFriendIdsAsync(string userId, CancellationToken ct = default) =>
        _friends.FriendIdsAsync(userId, ct);

    public Task<IReadOnlyList<string>> GetGroupMemberIdsAsync(string groupId, CancellationToken ct = default) =>
        _groups.MemberIdsAsync(groupId, ct);
}

/// <summary>Everything that has to happen before an account disappears (groups to hand over, parties, live connections).</summary>
public sealed class AccountCleanup
{
    private readonly IGroupCleanup _groups;
    private readonly IPartyMembership _parties;
    private readonly IRealtime _manager;

    public AccountCleanup(IGroupCleanup groups, IPartyMembership parties, IRealtime manager)
    {
        _groups = groups;
        _parties = parties;
        _manager = manager;
    }

    public async Task BeforeDeleteAsync(string userId, CancellationToken ct)
    {
        await _groups.OnUserDeletingAsync(userId, ct);
        await _parties.LeaveAsync(userId, ct);
        await _manager.DisconnectUserAsync(userId, "account_deleted");
    }
}

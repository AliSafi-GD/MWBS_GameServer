namespace GameServer.Core.Social;

/// <summary>Storage for groups. Business rules (who may do what) live in <see cref="GroupService"/>.</summary>
public interface IGroupStore
{
    /// <summary>Creates the group with the creator as owner. Throws group_name_taken.</summary>
    Task<GroupInfo> CreateAsync(NewGroup group, CancellationToken ct = default);

    Task<GroupInfo?> GetAsync(string groupId, CancellationToken ct = default);

    /// <summary>Applies the non-null fields. Returns null if the group does not exist. Throws group_name_taken.</summary>
    Task<GroupInfo?> UpdateAsync(string groupId, GroupPatch patch, CancellationToken ct = default);

    Task<bool> DeleteAsync(string groupId, CancellationToken ct = default);

    /// <summary>Groups whose name starts with <paramref name="query"/> (any group when empty), by name.</summary>
    Task<IReadOnlyList<GroupInfo>> SearchAsync(string? query, string? type, int limit, CancellationToken ct = default);

    /// <summary>The player's groups including pending requests and invites (not bans).</summary>
    Task<IReadOnlyList<(GroupInfo Group, GroupRole Role)>> ListForUserAsync(string userId, CancellationToken ct = default);

    Task<GroupRole?> GetRoleAsync(string groupId, string userId, CancellationToken ct = default);

    /// <summary>Sets any role (also requested, invited, banned) without a capacity check.</summary>
    Task SetRoleAsync(string groupId, string userId, GroupRole role, CancellationToken ct = default);

    /// <summary>Makes the player a member or admin, refusing with group_full when the group has no room.</summary>
    Task AddMemberAsync(string groupId, string userId, GroupRole role, CancellationToken ct = default);

    Task<bool> RemoveMemberAsync(string groupId, string userId, CancellationToken ct = default);

    /// <summary>Owner becomes admin, <paramref name="toUserId"/> becomes owner.</summary>
    Task TransferOwnershipAsync(string groupId, string fromUserId, string toUserId, CancellationToken ct = default);

    Task<IReadOnlyList<GroupMemberInfo>> ListMembersAsync(
        string groupId, GroupRole fromRole, GroupRole toRole, int limit, CancellationToken ct = default);

    /// <summary>Owner, admins and members.</summary>
    Task<IReadOnlyList<string>> MemberIdsAsync(string groupId, CancellationToken ct = default);

    /// <summary>How many groups of this type the player belongs to (as owner, admin or member).</summary>
    Task<int> CountMembershipsAsync(string userId, string type, CancellationToken ct = default);

    Task<IReadOnlyList<string>> OwnedGroupIdsAsync(string userId, CancellationToken ct = default);

    /// <summary>The admin (else the longest-standing member) who should inherit a group, or null if nobody else is in it.</summary>
    Task<string?> FindSuccessorAsync(string groupId, string excludingUserId, CancellationToken ct = default);
}

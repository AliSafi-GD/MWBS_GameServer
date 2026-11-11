namespace GameServer.Core.Social;

// ---- friends ----------------------------------------------------------------------------------------------

/// <summary>How the row owner sees the other player. Two rows per pair (one per side) keep listing simple.</summary>
public enum FriendState
{
    Friend = 0,
    InviteSent = 1,
    InviteReceived = 2,
    Blocked = 3
}

public sealed record FriendEntry(PublicProfile Profile, FriendState State, DateTime UpdatedUtc);

/// <param name="State">"friend", "invite_sent", "invite_received" or "blocked".</param>
public sealed record FriendView(PublicProfile Profile, string State, bool Online, DateTime UpdatedUtc);

public sealed record FriendResult(string UserId, string State);

/// <summary>Request of <c>friends.add</c>. Give either a user id or a username.</summary>
public sealed record FriendRequest(string? UserId, string? Username);

/// <summary>Request of operations that only name another player.</summary>
public sealed record TargetRequest(string UserId);

// ---- groups (clans, guilds...) ------------------------------------------------------------------------------

/// <summary>Lower number = higher rank. Requested/Invited/Banned are not members.</summary>
public enum GroupRole
{
    Owner = 0,
    Admin = 1,
    Member = 2,
    Requested = 3,
    Invited = 4,
    Banned = 5
}

public sealed record GroupInfo(
    string Id, string Name, string Description, string Avatar, string Type, bool Open,
    int MaxMembers, int MemberCount, string? CreatorId, DateTime CreatedUtc);

/// <param name="Role">"owner", "admin", "member", "requested", "invited" or "banned".</param>
public sealed record GroupMemberInfo(PublicProfile Profile, string Role, DateTime UpdatedUtc);

public sealed record MyGroup(GroupInfo Group, string Role);

public sealed record NewGroup(
    string Id, string Name, string Description, string Avatar, string Type, bool Open, int MaxMembers, string CreatorId);

public sealed record GroupPatch(string? Name, string? Description, string? Avatar, bool? Open, int? MaxMembers);

/// <summary>Request of <c>groups.create</c>. <c>Type</c> is a key of the "social" config (default types: "clan", "group").</summary>
public sealed record CreateGroupRequest(string? Name, string? Description, string? Avatar, string? Type, bool? Open, int? MaxMembers);

public sealed record GroupRequest(string GroupId);

public sealed record GroupTargetRequest(string GroupId, string UserId);

public sealed record GroupUpdateRequest(
    string GroupId, string? Name, string? Description, string? Avatar, bool? Open, int? MaxMembers);

// ---- parties --------------------------------------------------------------------------------------------------

public sealed record PartyMember(string UserId, string Username, string DisplayName, string Avatar);

public sealed record PartyView(
    string Id, string LeaderId, bool Open, int MaxSize, IReadOnlyList<PartyMember> Members, DateTime CreatedUtc);

/// <summary>Request of <c>parties.create</c>.</summary>
public sealed record CreatePartyRequest(bool? Open, int? MaxSize);

public sealed record PartyRequest(string PartyId);

using System.Globalization;
using GameServer.Abstractions;
using GameServer.Core.Realtime;

namespace GameServer.Core.Social;

/// <summary>
/// Groups (clans, guilds, teams): who may do what, limits from the "social" config, and live notifications.
/// Roles: owner &gt; admin &gt; member. An admin can accept requests, invite, kick and ban players of lower rank;
/// only the owner can promote, demote, hand over or delete.
/// </summary>
public sealed class GroupService
{
    private readonly IGroupStore _store;
    private readonly IConfigStore _config;
    private readonly IUserStore _users;
    private readonly ConnectionManager _manager;

    public GroupService(IGroupStore store, IConfigStore config, IUserStore users, ConnectionManager manager)
    {
        _store = store;
        _config = config;
        _users = users;
        _manager = manager;
    }

    public static string RoleName(GroupRole role) => role.ToString().ToLowerInvariant();

    public static string ChannelOf(string groupId) => "group:" + groupId;

    // ---- reading ------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<GroupInfo>> SearchAsync(string? query, string? type, int limit, CancellationToken ct) =>
        _store.SearchAsync(query, type, limit, ct);

    public Task<GroupInfo> GetAsync(string groupId, CancellationToken ct) => RequireGroupAsync(groupId, ct);

    public async Task<IReadOnlyList<MyGroup>> MineAsync(string userId, CancellationToken ct) =>
        (await _store.ListForUserAsync(userId, ct)).Select(g => new MyGroup(g.Group, RoleName(g.Role))).ToList();

    /// <summary>Members of a group; with <paramref name="pending"/> the open requests and invites instead (admins only).</summary>
    public async Task<IReadOnlyList<GroupMemberInfo>> MembersAsync(
        string actorId, string groupId, bool pending, int limit, CancellationToken ct)
    {
        var group = await RequireGroupAsync(groupId, ct);
        var role = await _store.GetRoleAsync(groupId, actorId, ct);

        if (pending)
        {
            if (role is null || role.Value > GroupRole.Admin)
                throw new GameException("not_group_admin", "Only group admins can see requests and invites.");
            return await _store.ListMembersAsync(groupId, GroupRole.Requested, GroupRole.Invited, limit, ct);
        }

        var isMember = role is not null && role.Value <= GroupRole.Member;
        if (!isMember && !group.Open)
            throw new GameException("not_a_member", "Only members can see who is in a closed group.");

        return await _store.ListMembersAsync(groupId, GroupRole.Owner, GroupRole.Member, limit, ct);
    }

    // ---- creating and changing ----------------------------------------------------------------------------

    public async Task<GroupInfo> CreateAsync(string actorId, CreateGroupRequest request, CancellationToken ct)
    {
        var rules = await RulesAsync(ct);

        var type = (request.Type ?? "group").Trim().ToLowerInvariant();
        if (!rules.GroupTypes.TryGetValue(type, out var rule))
            throw new GameException("invalid_group_type", $"Unknown group type. Allowed: {string.Join(", ", rules.GroupTypes.Keys)}.");

        var name = NormalizeName(request.Name);
        var description = NormalizeDescription(request.Description) ?? "";
        var avatar = request.Avatar is null ? "" : ProfileValidator.NormalizeAvatar(request.Avatar);
        var maxMembers = Math.Clamp(request.MaxMembers ?? rule.MaxMembers, 2, rule.MaxMembers);

        if (await _store.CountMembershipsAsync(actorId, type, ct) >= rule.MaxPerUser)
            throw new GameException("group_limit_reached", $"You can only be in {rule.MaxPerUser} group(s) of type '{type}'.");

        return await _store.CreateAsync(
            new NewGroup(Guid.NewGuid().ToString("N"), name, description, avatar, type, request.Open ?? true, maxMembers, actorId), ct);
    }

    public async Task<GroupInfo> UpdateAsync(string actorId, GroupUpdateRequest request, CancellationToken ct)
    {
        var group = await RequireGroupAsync(request.GroupId, ct);
        await RequireAdminAsync(group.Id, actorId, ct);
        var rule = (await RulesAsync(ct)).RuleFor(group.Type);

        int? maxMembers = null;
        if (request.MaxMembers is { } requested)
        {
            if (requested < Math.Max(2, group.MemberCount) || requested > rule.MaxMembers)
                throw new GameException("invalid_request", $"maxMembers must be between {Math.Max(2, group.MemberCount)} and {rule.MaxMembers}.");
            maxMembers = requested;
        }

        var patch = new GroupPatch(
            request.Name is null ? null : NormalizeName(request.Name),
            NormalizeDescription(request.Description),
            request.Avatar is null ? null : ProfileValidator.NormalizeAvatar(request.Avatar),
            request.Open,
            maxMembers);

        return await _store.UpdateAsync(group.Id, patch, ct) ?? throw new GameException("group_not_found", "Group not found.");
    }

    public async Task DeleteAsync(string actorId, string groupId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        await RequireOwnerAsync(groupId, actorId, ct);

        var memberIds = await _store.MemberIdsAsync(groupId, ct);
        await _store.DeleteAsync(groupId, ct);

        foreach (var id in memberIds)
        {
            _manager.RemoveUserFromChannel(id, ChannelOf(groupId));
            if (id != actorId)
                await PushAsync(id, "group.deleted", new { groupId }, ct);
        }
    }

    // ---- joining and leaving ------------------------------------------------------------------------------

    /// <summary>Joins an open group, answers an invite, or sends a request to a closed one. Returns the resulting role.</summary>
    public async Task<string> JoinAsync(string actorId, string groupId, CancellationToken ct)
    {
        var group = await RequireGroupAsync(groupId, ct);
        var role = await _store.GetRoleAsync(groupId, actorId, ct);

        switch (role)
        {
            case GroupRole.Banned:
                throw new GameException("banned_from_group", "You were banned from this group.");
            case GroupRole.Owner or GroupRole.Admin or GroupRole.Member:
                throw new GameException("already_member", "You are already in this group.");
            case GroupRole.Requested:
                throw new GameException("already_requested", "Your request is waiting for an answer.");
        }

        if (role == GroupRole.Invited || group.Open)
        {
            await EnsureRoomForAsync(actorId, group, ct);
            await _store.AddMemberAsync(groupId, actorId, GroupRole.Member, ct);
            return RoleName(GroupRole.Member);
        }

        await _store.SetRoleAsync(groupId, actorId, GroupRole.Requested, ct);
        foreach (var adminId in await AdminIdsAsync(groupId, ct))
            await PushAsync(adminId, "group.request", new { groupId, userId = actorId }, ct);
        return RoleName(GroupRole.Requested);
    }

    /// <summary>Leaves a group, or withdraws a request / declines an invite. The owner can only leave an otherwise empty group.</summary>
    public async Task LeaveAsync(string actorId, string groupId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        var role = await _store.GetRoleAsync(groupId, actorId, ct)
                   ?? throw new GameException("not_a_member", "You are not in this group.");

        if (role == GroupRole.Banned)
            throw new GameException("banned_from_group", "You were banned from this group.");

        if (role == GroupRole.Owner)
        {
            if ((await _store.MemberIdsAsync(groupId, ct)).Count > 1)
                throw new GameException("owner_cannot_leave", "Hand the group over to someone else first.");
            await _store.DeleteAsync(groupId, ct);
        }
        else
        {
            await _store.RemoveMemberAsync(groupId, actorId, ct);
        }

        _manager.RemoveUserFromChannel(actorId, ChannelOf(groupId));
    }

    // ---- managing others ----------------------------------------------------------------------------------

    public async Task InviteAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        var group = await RequireGroupAsync(groupId, ct);
        await RequireAdminAsync(groupId, actorId, ct);

        if (targetId == actorId)
            throw new GameException("invalid_request", "You cannot invite yourself.");
        await RequireActiveUserAsync(targetId, ct);

        var targetRole = await _store.GetRoleAsync(groupId, targetId, ct);
        switch (targetRole)
        {
            case GroupRole.Banned:
                throw new GameException("banned_from_group", "That player is banned from this group.");
            case GroupRole.Owner or GroupRole.Admin or GroupRole.Member:
                throw new GameException("already_member", "That player is already in this group.");
            case GroupRole.Invited:
                return;
            case GroupRole.Requested:
                await AcceptAsync(actorId, groupId, targetId, ct); // they asked already: inviting them is accepting
                return;
        }

        await _store.SetRoleAsync(groupId, targetId, GroupRole.Invited, ct);
        await PushAsync(targetId, "group.invite", new { groupId, groupName = group.Name, fromUserId = actorId }, ct);
    }

    public async Task AcceptAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        var group = await RequireGroupAsync(groupId, ct);
        await RequireAdminAsync(groupId, actorId, ct);

        var targetRole = await _store.GetRoleAsync(groupId, targetId, ct);
        if (targetRole != GroupRole.Requested)
            throw new GameException("not_found", "That player has no open request.");

        await EnsureRoomForAsync(targetId, group, ct);
        await _store.AddMemberAsync(groupId, targetId, GroupRole.Member, ct);
        await PushAsync(targetId, "group.accepted", new { groupId, groupName = group.Name }, ct);
    }

    /// <summary>Removes a member, or rejects a request / revokes an invite. The target must have a lower rank.</summary>
    public async Task KickAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        var actorRole = await RequireAdminAsync(groupId, actorId, ct);

        if (targetId == actorId)
            throw new GameException("invalid_request", "Use leave instead.");

        var targetRole = await _store.GetRoleAsync(groupId, targetId, ct)
                         ?? throw new GameException("not_found", "That player is not in this group.");
        RequireOutranks(actorRole, targetRole);

        await _store.RemoveMemberAsync(groupId, targetId, ct);
        _manager.RemoveUserFromChannel(targetId, ChannelOf(groupId));

        if (targetRole <= GroupRole.Member)
            await PushAsync(targetId, "group.kicked", new { groupId }, ct);
    }

    public async Task BanAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        var actorRole = await RequireAdminAsync(groupId, actorId, ct);

        if (targetId == actorId)
            throw new GameException("invalid_request", "You cannot ban yourself.");
        await RequireActiveUserAsync(targetId, ct);

        var targetRole = await _store.GetRoleAsync(groupId, targetId, ct);
        if (targetRole is { } existing)
            RequireOutranks(actorRole, existing);

        await _store.SetRoleAsync(groupId, targetId, GroupRole.Banned, ct);
        _manager.RemoveUserFromChannel(targetId, ChannelOf(groupId));

        if (targetRole is not null && targetRole.Value <= GroupRole.Member)
            await PushAsync(targetId, "group.banned", new { groupId }, ct);
    }

    public async Task UnbanAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        await RequireAdminAsync(groupId, actorId, ct);

        if (await _store.GetRoleAsync(groupId, targetId, ct) != GroupRole.Banned)
            throw new GameException("not_found", "That player is not banned.");

        await _store.RemoveMemberAsync(groupId, targetId, ct);
    }

    public async Task PromoteAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        await RequireOwnerAsync(groupId, actorId, ct);

        if (await _store.GetRoleAsync(groupId, targetId, ct) != GroupRole.Member)
            throw new GameException("invalid_request", "Only a regular member can be promoted to admin.");

        await _store.SetRoleAsync(groupId, targetId, GroupRole.Admin, ct);
        await PushAsync(targetId, "group.role_changed", new { groupId, role = RoleName(GroupRole.Admin) }, ct);
    }

    public async Task DemoteAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        await RequireOwnerAsync(groupId, actorId, ct);

        if (await _store.GetRoleAsync(groupId, targetId, ct) != GroupRole.Admin)
            throw new GameException("invalid_request", "Only an admin can be demoted.");

        await _store.SetRoleAsync(groupId, targetId, GroupRole.Member, ct);
        await PushAsync(targetId, "group.role_changed", new { groupId, role = RoleName(GroupRole.Member) }, ct);
    }

    public async Task TransferAsync(string actorId, string groupId, string targetId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);
        await RequireOwnerAsync(groupId, actorId, ct);

        if (targetId == actorId)
            throw new GameException("invalid_request", "You already own this group.");

        await _store.TransferOwnershipAsync(groupId, actorId, targetId, ct);
        await PushAsync(targetId, "group.role_changed", new { groupId, role = RoleName(GroupRole.Owner) }, ct);
    }

    /// <summary>Before an account is deleted: hand its groups to the next in line, or delete them when nobody else is left.</summary>
    public async Task OnUserDeletingAsync(string userId, CancellationToken ct)
    {
        foreach (var groupId in await _store.OwnedGroupIdsAsync(userId, ct))
        {
            var successor = await _store.FindSuccessorAsync(groupId, userId, ct);
            if (successor is null)
                await _store.DeleteAsync(groupId, ct);
            else
                await _store.TransferOwnershipAsync(groupId, userId, successor, ct);
        }
    }

    // ---- admin (called by /admin/api; no player permission checks) ---------------------------------------

    /// <summary>All members including pending and banned ones.</summary>
    public Task<IReadOnlyList<GroupMemberInfo>> AdminMembersAsync(string groupId, int limit, CancellationToken ct) =>
        _store.ListMembersAsync(groupId, GroupRole.Owner, GroupRole.Banned, limit, ct);

    public async Task<GroupInfo> AdminUpdateAsync(string groupId, GroupUpdateRequest request, CancellationToken ct)
    {
        var group = await RequireGroupAsync(groupId, ct);

        int? maxMembers = null;
        if (request.MaxMembers is { } requested)
        {
            if (requested < Math.Max(2, group.MemberCount) || requested > 10_000)
                throw new GameException("invalid_request", $"maxMembers must be between {Math.Max(2, group.MemberCount)} and 10000.");
            maxMembers = requested;
        }

        var patch = new GroupPatch(
            request.Name is null ? null : NormalizeName(request.Name),
            NormalizeDescription(request.Description),
            request.Avatar is null ? null : ProfileValidator.NormalizeAvatar(request.Avatar),
            request.Open,
            maxMembers);

        return await _store.UpdateAsync(group.Id, patch, ct) ?? throw new GameException("group_not_found", "Group not found.");
    }

    public async Task AdminDeleteAsync(string groupId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);

        var memberIds = await _store.MemberIdsAsync(groupId, ct);
        await _store.DeleteAsync(groupId, ct);

        foreach (var id in memberIds)
        {
            _manager.RemoveUserFromChannel(id, ChannelOf(groupId));
            await PushAsync(id, "group.deleted", new { groupId }, ct);
        }
    }

    /// <summary>Removes a member (or bans them). The owner cannot be removed: transfer ownership first.</summary>
    public async Task AdminRemoveMemberAsync(string groupId, string targetId, bool ban, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);

        var role = await _store.GetRoleAsync(groupId, targetId, ct);
        if (role is null && !ban)
            throw new GameException("not_found", "That player is not in this group.");
        if (role == GroupRole.Owner)
            throw new GameException("owner_cannot_leave", "Transfer ownership before removing the owner.");

        if (ban)
            await _store.SetRoleAsync(groupId, targetId, GroupRole.Banned, ct);
        else
            await _store.RemoveMemberAsync(groupId, targetId, ct);

        _manager.RemoveUserFromChannel(targetId, ChannelOf(groupId));
        if (role is { } r && r <= GroupRole.Member)
            await PushAsync(targetId, ban ? "group.banned" : "group.kicked", new { groupId }, ct);
    }

    /// <summary>Makes a member the owner (the previous owner becomes an admin; if there is none, the group simply gets one).</summary>
    public async Task AdminTransferAsync(string groupId, string newOwnerId, CancellationToken ct)
    {
        await RequireGroupAsync(groupId, ct);

        var targetRole = await _store.GetRoleAsync(groupId, newOwnerId, ct);
        if (targetRole is null || targetRole.Value > GroupRole.Member)
            throw new GameException("not_a_member", "The new owner must be a member of the group.");
        if (targetRole == GroupRole.Owner)
            return;

        var owners = await _store.ListMembersAsync(groupId, GroupRole.Owner, GroupRole.Owner, 1, ct);
        if (owners.Count == 0)
            await _store.SetRoleAsync(groupId, newOwnerId, GroupRole.Owner, ct);
        else
            await _store.TransferOwnershipAsync(groupId, owners[0].Profile.Id, newOwnerId, ct);

        await PushAsync(newOwnerId, "group.role_changed", new { groupId, role = RoleName(GroupRole.Owner) }, ct);
    }

    // ---- rules and helpers --------------------------------------------------------------------------------

    private async Task<SocialRules> RulesAsync(CancellationToken ct) =>
        SocialRules.From((await _config.GetAsync(ConfigRules.Social, ct))?.Value);

    private async Task<GroupInfo> RequireGroupAsync(string groupId, CancellationToken ct) =>
        await _store.GetAsync(groupId, ct) ?? throw new GameException("group_not_found", "Group not found.");

    private async Task RequireActiveUserAsync(string userId, CancellationToken ct)
    {
        var user = await _users.GetAsync(userId, ct);
        if (user is null || user.Disabled)
            throw new GameException("not_found", "Player not found.");
    }

    /// <summary>Checks the per-type limit of the player and that the group still has room.</summary>
    private async Task EnsureRoomForAsync(string userId, GroupInfo group, CancellationToken ct)
    {
        var rule = (await RulesAsync(ct)).RuleFor(group.Type);
        if (await _store.CountMembershipsAsync(userId, group.Type, ct) >= rule.MaxPerUser)
            throw new GameException("group_limit_reached", $"A player can only be in {rule.MaxPerUser} group(s) of type '{group.Type}'.");
    }

    private async Task<GroupRole> RequireMemberAsync(string groupId, string userId, CancellationToken ct)
    {
        var role = await _store.GetRoleAsync(groupId, userId, ct);
        if (role is null || role.Value > GroupRole.Member)
            throw new GameException("not_a_member", "You are not a member of this group.");
        return role.Value;
    }

    private async Task<GroupRole> RequireAdminAsync(string groupId, string userId, CancellationToken ct)
    {
        var role = await RequireMemberAsync(groupId, userId, ct);
        if (role > GroupRole.Admin)
            throw new GameException("not_group_admin", "Only group admins can do this.");
        return role;
    }

    private async Task RequireOwnerAsync(string groupId, string userId, CancellationToken ct)
    {
        var role = await RequireMemberAsync(groupId, userId, ct);
        if (role != GroupRole.Owner)
            throw new GameException("not_group_owner", "Only the group owner can do this.");
    }

    private static void RequireOutranks(GroupRole actor, GroupRole target)
    {
        if (actor >= target)
            throw new GameException("insufficient_rank", "You cannot manage a player of equal or higher rank.");
    }

    private async Task<IReadOnlyList<string>> AdminIdsAsync(string groupId, CancellationToken ct) =>
        (await _store.ListMembersAsync(groupId, GroupRole.Owner, GroupRole.Admin, 50, ct)).Select(m => m.Profile.Id).ToList();

    private Task PushAsync(string userId, string type, object data, CancellationToken ct) =>
        _manager.SendToUserAsync(userId, Frames.Build(type, null, data), ct);

    private static string NormalizeName(string? input)
    {
        var name = (input ?? "").Trim();
        if (name.Length > 200 || new StringInfo(name).LengthInTextElements is < 3 or > 32)
            throw new GameException("invalid_group_name", "A group name must be 3-32 characters.");
        if (!TextRules.IsClean(name))
            throw new GameException("invalid_group_name", "The group name contains invalid characters.");
        return name;
    }

    private static string? NormalizeDescription(string? input)
    {
        if (input is null)
            return null;

        var text = input.Trim();
        if (text.Length > 300)
            throw new GameException("invalid_group_description", "A description can be at most 300 characters.");
        if (!TextRules.IsClean(text, allowNewlines: true))
            throw new GameException("invalid_group_description", "The description contains invalid characters.");
        return text;
    }
}

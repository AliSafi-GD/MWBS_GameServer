using GameServer.Abstractions;
using GameServer.Core.Realtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core.Social;

/// <summary>Bound from "GameServer:Parties".</summary>
public sealed class PartyOptions
{
    /// <summary>Upper limit for the size of a party.</summary>
    public int MaxSize { get; set; } = 8;

    /// <summary>How long a player may be disconnected before they are taken out of their party.</summary>
    public int GraceSeconds { get; set; } = 15;

    public int InviteMinutes { get; set; } = 5;

    /// <summary>Creating or joining needs an open realtime connection (parties live only while players are connected).</summary>
    public bool RequireConnection { get; set; } = true;
}

/// <summary>
/// Short-lived teams that gather before a match. They live in memory on this server instance only: they are gone
/// when the server restarts, and a player must be connected to a party's instance to be in it.
/// Every change is pushed to all members as a <c>party.update</c> event; party chat is the channel <c>party:&lt;id&gt;</c>.
/// </summary>
public sealed class PartyService
{
    private sealed class Party
    {
        public Party(string id, string leaderId, bool open, int maxSize)
        {
            Id = id;
            LeaderId = leaderId;
            Open = open;
            MaxSize = maxSize;
            CreatedUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public string LeaderId { get; set; }
        public bool Open { get; }
        public int MaxSize { get; }
        public DateTime CreatedUtc { get; }
        public List<PartyMember> Members { get; } = new();
        public Dictionary<string, DateTime> Invites { get; } = new(StringComparer.Ordinal);

        public PartyView ToView() => new(Id, LeaderId, Open, MaxSize, Members.ToList(), CreatedUtc);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Party> _parties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _partyOf = new(StringComparer.Ordinal); // user id -> party id

    private readonly ConnectionManager _manager;
    private readonly IUserStore _users;
    private readonly PartyOptions _options;
    private readonly ILogger<PartyService> _log;

    public PartyService(ConnectionManager manager, IUserStore users, IOptions<PartyOptions> options, ILogger<PartyService> log)
    {
        _manager = manager;
        _users = users;
        _options = options.Value;
        _log = log;

        _manager.UserWentOffline += userId => _ = LeaveAfterGraceAsync(userId);
    }

    public static string ChannelOf(string partyId) => "party:" + partyId;

    public bool IsMember(string partyId, string userId)
    {
        lock (_gate)
            return _partyOf.TryGetValue(userId, out var id) && id == partyId;
    }

    public PartyView? Mine(string userId)
    {
        lock (_gate)
            return _partyOf.TryGetValue(userId, out var id) ? _parties[id].ToView() : null;
    }

    /// <summary>A party can be read by its members, by anyone when it is open, and by players invited to it.</summary>
    public PartyView Get(string partyId, string requesterId)
    {
        lock (_gate)
        {
            if (!_parties.TryGetValue(partyId, out var party))
                throw new GameException("party_not_found", "Party not found.");

            var allowed = party.Open
                          || party.Members.Any(m => m.UserId == requesterId)
                          || (party.Invites.TryGetValue(requesterId, out var expires) && expires > DateTime.UtcNow);
            if (!allowed)
                throw new GameException("party_not_found", "Party not found.");

            return party.ToView();
        }
    }

    public async Task<PartyView> CreateAsync(string userId, bool open, int? maxSize, CancellationToken ct)
    {
        RequireConnection(userId);
        var member = await ProfileAsync(userId, ct);

        lock (_gate)
        {
            if (_partyOf.ContainsKey(userId))
                throw new GameException("already_in_party", "Leave your current party first.");

            var party = new Party(
                Guid.NewGuid().ToString("N"), userId, open, Math.Clamp(maxSize ?? _options.MaxSize, 2, _options.MaxSize));
            party.Members.Add(member);
            _parties[party.Id] = party;
            _partyOf[userId] = party.Id;
            return party.ToView();
        }
    }

    public async Task<PartyView> JoinAsync(string userId, string partyId, CancellationToken ct)
    {
        RequireConnection(userId);
        var member = await ProfileAsync(userId, ct);

        PartyView view;
        lock (_gate)
        {
            if (_partyOf.ContainsKey(userId))
                throw new GameException("already_in_party", "Leave your current party first.");
            if (!_parties.TryGetValue(partyId, out var party))
                throw new GameException("party_not_found", "Party not found.");

            var invited = party.Invites.TryGetValue(userId, out var expires) && expires > DateTime.UtcNow;
            if (!party.Open && !invited)
                throw new GameException("not_invited", "This party is closed. Ask a member to invite you.");
            if (party.Members.Count >= party.MaxSize)
                throw new GameException("party_full", "This party is full.");

            party.Invites.Remove(userId);
            party.Members.Add(member);
            _partyOf[userId] = party.Id;
            view = party.ToView();
        }

        await PushUpdateAsync(view, ct);
        return view;
    }

    /// <summary>Leaves the player's party, if any. Returns whether they were in one.</summary>
    public Task<bool> LeaveAsync(string userId, CancellationToken ct) => RemoveMemberAsync(userId, "left", ct);

    public async Task InviteAsync(string inviterId, string targetId, CancellationToken ct)
    {
        if (targetId == inviterId)
            throw new GameException("invalid_request", "You cannot invite yourself.");
        if (!_manager.IsOnline(targetId))
            throw new GameException("not_online", "That player is not online.");

        var inviter = await ProfileAsync(inviterId, ct);
        PartyView view;
        DateTime expiresUtc;

        lock (_gate)
        {
            var party = PartyOf(inviterId);
            if (!party.Open && party.LeaderId != inviterId)
                throw new GameException("not_party_leader", "Only the party leader can invite to a closed party.");
            if (_partyOf.ContainsKey(targetId))
                throw new GameException("already_in_party", "That player is already in a party.");
            if (party.Members.Count >= party.MaxSize)
                throw new GameException("party_full", "This party is full.");

            // Forget invites that ran out so the list cannot grow without limit.
            foreach (var expired in party.Invites.Where(i => i.Value <= DateTime.UtcNow).Select(i => i.Key).ToList())
                party.Invites.Remove(expired);
            if (party.Invites.Count >= 20)
                throw new GameException("too_many_invites", "This party has too many open invites.");

            expiresUtc = DateTime.UtcNow.AddMinutes(_options.InviteMinutes);
            party.Invites[targetId] = expiresUtc;
            view = party.ToView();
        }

        await _manager.SendToUserAsync(
            targetId, Frames.Build("party.invite", null, new { partyId = view.Id, from = inviter, expiresUtc }), ct);
    }

    public async Task KickAsync(string leaderId, string targetId, CancellationToken ct)
    {
        lock (_gate)
        {
            var party = PartyOf(leaderId);
            RequireLeader(party, leaderId);
            if (targetId == leaderId || party.Members.All(m => m.UserId != targetId))
                throw new GameException("not_found", "That player is not in your party.");
        }

        await RemoveMemberAsync(targetId, "kicked", ct);
    }

    public async Task PromoteAsync(string leaderId, string targetId, CancellationToken ct)
    {
        PartyView view;
        lock (_gate)
        {
            var party = PartyOf(leaderId);
            RequireLeader(party, leaderId);
            if (party.Members.All(m => m.UserId != targetId))
                throw new GameException("not_found", "That player is not in your party.");

            party.LeaderId = targetId;
            view = party.ToView();
        }

        await PushUpdateAsync(view, ct);
    }

    /// <summary>Disbands the party. Only the leader can.</summary>
    public async Task CloseAsync(string leaderId, CancellationToken ct)
    {
        Party party;
        lock (_gate)
        {
            party = PartyOf(leaderId);
            RequireLeader(party, leaderId);

            _parties.Remove(party.Id);
            foreach (var member in party.Members)
                _partyOf.Remove(member.UserId);
        }

        foreach (var member in party.Members)
        {
            _manager.RemoveUserFromChannel(member.UserId, ChannelOf(party.Id));
            await _manager.SendToUserAsync(
                member.UserId, Frames.Build("party.left", null, new { partyId = party.Id, reason = "closed" }), ct);
        }
    }

    // ---- internals ----------------------------------------------------------------------------------------

    private async Task<bool> RemoveMemberAsync(string userId, string reason, CancellationToken ct)
    {
        PartyView? view = null;
        string partyId;

        lock (_gate)
        {
            if (!_partyOf.Remove(userId, out var removedPartyId))
                return false;

            partyId = removedPartyId;
            var party = _parties[partyId];
            party.Members.RemoveAll(m => m.UserId == userId);

            if (party.Members.Count == 0)
            {
                _parties.Remove(partyId);
            }
            else
            {
                if (party.LeaderId == userId)
                    party.LeaderId = party.Members[0].UserId;
                view = party.ToView();
            }
        }

        _manager.RemoveUserFromChannel(userId, ChannelOf(partyId));
        await _manager.SendToUserAsync(userId, Frames.Build("party.left", null, new { partyId, reason }), ct);

        if (view is not null)
            await PushUpdateAsync(view, ct);
        return true;
    }

    private Task PushUpdateAsync(PartyView view, CancellationToken ct) =>
        _manager.SendToUsersAsync(view.Members.Select(m => m.UserId), Frames.Build("party.update", null, view), ct);

    private async Task LeaveAfterGraceAsync(string userId)
    {
        try
        {
            lock (_gate)
            {
                if (!_partyOf.ContainsKey(userId))
                    return;
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.GraceSeconds));
            if (_manager.IsOnline(userId))
                return; // they reconnected in time

            await RemoveMemberAsync(userId, "disconnected", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Removing a disconnected player from their party failed.");
        }
    }

    private void RequireConnection(string userId)
    {
        if (_options.RequireConnection && !_manager.IsOnline(userId))
            throw new GameException("party_requires_connection", "Connect to the realtime server before using parties.");
    }

    private Party PartyOf(string userId)
    {
        if (!_partyOf.TryGetValue(userId, out var id))
            throw new GameException("party_not_found", "You are not in a party.");
        return _parties[id];
    }

    private static void RequireLeader(Party party, string userId)
    {
        if (party.LeaderId != userId)
            throw new GameException("not_party_leader", "Only the party leader can do this.");
    }

    private async Task<PartyMember> ProfileAsync(string userId, CancellationToken ct)
    {
        var profile = (await _users.GetPublicAsync(new[] { userId }, ct)).FirstOrDefault()
                      ?? throw new GameException("not_found", "Player not found.");
        return new PartyMember(profile.Id, profile.Username, profile.DisplayName, profile.Avatar);
    }
}

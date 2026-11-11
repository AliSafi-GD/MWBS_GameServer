using GameServer.Abstractions;
using GameServer.Core.Realtime;

namespace GameServer.Core.Social;

/// <summary>Friend rules on top of <see cref="IFriendStore"/>: who can be added, limits, and telling the other player live.</summary>
public sealed class FriendsService
{
    private readonly IFriendStore _store;
    private readonly IUserStore _users;
    private readonly IConfigStore _config;
    private readonly ConnectionManager _manager;

    public FriendsService(IFriendStore store, IUserStore users, IConfigStore config, ConnectionManager manager)
    {
        _store = store;
        _users = users;
        _config = config;
        _manager = manager;
    }

    public static string StateName(FriendState state) => state switch
    {
        FriendState.Friend => "friend",
        FriendState.InviteSent => "invite_sent",
        FriendState.InviteReceived => "invite_received",
        _ => "blocked"
    };

    public async Task<IReadOnlyList<FriendView>> ListAsync(string userId, FriendState? state, int limit, CancellationToken ct)
    {
        var entries = await _store.ListAsync(userId, state, limit, ct);
        return entries
            .Select(e => new FriendView(
                e.Profile, StateName(e.State), e.State == FriendState.Friend && _manager.IsOnline(e.Profile.Id), e.UpdatedUtc))
            .ToList();
    }

    /// <summary>Sends an invite, or accepts one the other player sent. Pushes <c>friend.request</c> / <c>friend.accepted</c> to them.</summary>
    public async Task<FriendResult> AddAsync(string userId, FriendRequest request, CancellationToken ct)
    {
        var targetId = await ResolveAsync(request, ct);
        if (targetId == userId)
            throw new GameException("invalid_request", "You cannot add yourself.");

        var target = await _users.GetAsync(targetId, ct);
        if (target is null || target.Disabled)
            throw new GameException("not_found", "Player not found.");

        var rules = await RulesAsync(ct);
        var (state, changed) = await _store.AddAsync(userId, targetId, rules.MaxFriends, rules.MaxPendingInvites, ct);

        if (changed)
        {
            var me = (await _users.GetPublicAsync(new[] { userId }, ct)).FirstOrDefault();
            if (me is not null)
            {
                var type = state == FriendState.Friend ? "friend.accepted" : "friend.request";
                await _manager.SendToUserAsync(targetId, Frames.Build(type, null, new { user = me }), ct);
            }
        }

        return new FriendResult(targetId, StateName(state));
    }

    /// <summary>Removes a friend or cancels/declines an invite. The other player is not told.</summary>
    public Task<bool> RemoveAsync(string userId, string targetId, CancellationToken ct) =>
        _store.RemoveAsync(userId, targetId, ct);

    public async Task BlockAsync(string userId, string targetId, CancellationToken ct)
    {
        if (targetId == userId)
            throw new GameException("invalid_request", "You cannot block yourself.");

        var target = await _users.GetAsync(targetId, ct);
        if (target is null)
            throw new GameException("not_found", "Player not found.");

        await _store.BlockAsync(userId, targetId, ct);
    }

    public Task<bool> UnblockAsync(string userId, string targetId, CancellationToken ct) =>
        _store.UnblockAsync(userId, targetId, ct);

    private async Task<SocialRules> RulesAsync(CancellationToken ct) =>
        SocialRules.From((await _config.GetAsync(ConfigRules.Social, ct))?.Value);

    private async Task<string> ResolveAsync(FriendRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.UserId))
            return request.UserId.Trim();

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            return await _store.FindUserIdByUsernameAsync(request.Username.Trim().ToLowerInvariant(), ct)
                   ?? throw new GameException("not_found", "Player not found.");
        }

        throw new GameException("invalid_request", "userId or username is required.");
    }
}

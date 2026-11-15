using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GameServer.Abstractions;
using GameServer.Core.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core.Realtime;

internal enum ChannelKind
{
    Room,
    Direct,
    Group,
    Party
}

/// <param name="Name">The canonical channel name used on the wire and in storage.</param>
/// <param name="Target">The group id, party id or (for direct messages) the other player.</param>
internal sealed record ChannelRef(ChannelKind Kind, string Name, string? Target = null, string? UserA = null, string? UserB = null);

/// <summary>
/// Chat logic that does not depend on the transport. Channels:
/// <c>room:&lt;name&gt;</c> (anyone), <c>dm:&lt;userId&gt;</c> (two players), <c>group:&lt;groupId&gt;</c> (members) and
/// <c>party:&lt;partyId&gt;</c> (party members). Direct and group messages are stored; rooms only if configured; party chat never.
/// </summary>
public sealed class ChatService : IChat
{
    private static readonly Regex RoomRx = new(@"^[a-z0-9][a-z0-9_\-]{0,39}\z", RegexOptions.Compiled);
    private static readonly Regex IdRx = new(@"^[A-Za-z0-9\-]{1,64}\z", RegexOptions.Compiled);

    private sealed class RateWindow
    {
        public long StartMs;
        public int Count;
    }

    private readonly IRealtime _manager;
    private readonly IChannelSubscriptions _channels;
    private readonly IChatStore _store;
    private readonly IAccountStore _users;
    private readonly IFriendStore _friends;
    private readonly IGroupStore _groups;
    private readonly IPartyMembership _parties;
    private readonly ChatOptions _options;
    private readonly ILogger<ChatService> _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, RateWindow> _rates = new(StringComparer.Ordinal);
    private int _storedSinceCleanup;

    private sealed record MuteState(long CheckedMs, MuteInfo? Mute);
    private readonly ConcurrentDictionary<string, MuteState> _mutes = new(StringComparer.Ordinal);
    private const long MuteCacheMs = 30_000;

    public ChatService(
        IRealtime manager, IChannelSubscriptions channels, IChatStore store, IAccountStore users, IFriendStore friends, IGroupStore groups,
        IPartyMembership parties, IOptions<ChatOptions> options, ILogger<ChatService> log,
        TimeProvider time)
    {
        _time = time;
        _manager = manager;
        _channels = channels;
        _store = store;
        _users = users;
        _friends = friends;
        _groups = groups;
        _parties = parties;
        _options = options.Value;
        _log = log;

        _manager.UserWentOffline += userId => _rates.TryRemove(userId, out _);
    }

    // ---- what clients do ----------------------------------------------------------------------------------

    /// <summary>Starts listening to a channel on one connection. Returns the canonical channel name.</summary>
    public async Task<string> JoinAsync(IConnection connection, string userId, string? channel, CancellationToken ct)
    {
        var target = ParseChannel(userId, channel);
        await AuthorizeAsync(target, userId, ct);

        // Direct messages need no join: they go to every connection of both players.
        if (target.Kind != ChannelKind.Direct && !_channels.Join(connection, target.Name))
            throw new GameException("too_many_channels", $"A connection can listen to at most {ConnectionManager.MaxChannelsPerConnection} channels.");

        return target.Name;
    }

    public string Leave(IConnection connection, string userId, string? channel)
    {
        var target = ParseChannel(userId, channel);
        _channels.Leave(connection, target.Name);
        return target.Name;
    }

    public async Task<ChatMessageView> SendAsync(
        string userId, string userName, string? channel, string? text, CancellationToken ct)
    {
        var clean = CleanText(text);
        var target = ParseChannel(userId, channel);

        await EnsureNotMutedAsync(userId, ct);

        if (!AllowSend(userId))
            throw new GameException("rate_limited", "You are sending messages too fast.");

        await AuthorizeAsync(target, userId, ct);
        if (target.Kind != ChannelKind.Direct && !_manager.IsInChannel(userId, target.Name))
            throw new GameException("not_in_channel", "Join the channel before sending to it.");

        return await DeliverAsync(target, userId, userName, clean, ct);
    }

    /// <summary>Stored messages of a channel, newest first (pass the oldest id you have as <paramref name="before"/> for the next page).</summary>
    public async Task<IReadOnlyList<ChatMessageView>> HistoryAsync(
        string userId, string? channel, int limit, long? before, CancellationToken ct)
    {
        var target = ParseChannel(userId, channel);
        await AuthorizeAsync(target, userId, ct);

        if (!IsStored(target.Kind))
            return Array.Empty<ChatMessageView>();

        if (_options.RetentionDays > 0)
            await _store.DeleteOlderThanAsync(target.Name, _options.RetentionDays, ct);

        return await ViewRowsAsync(target.Name, limit, before, ct);
    }

    // ---- conversations and read marks ------------------------------------------------------------------------

    /// <summary>Unread counts stop here; clients show "99+" and the like.</summary>
    public const int UnreadCap = 100;

    /// <summary>
    /// The player's stored conversations (direct chats and clans they are a member of), newest first, with the last
    /// message and the unread count. One call instead of reading every friend's history.
    /// </summary>
    public async Task<IReadOnlyList<ConversationView>> ConversationsAsync(string userId, int limit, CancellationToken ct)
    {
        var groups = (await _groups.ListForUserAsync(userId, ct))
            .Where(g => g.Role <= GroupRole.Member)
            .ToDictionary(g => "group:" + g.Group.Id, g => g.Group);

        var rows = await _store.ConversationsAsync(userId, groups.Keys.ToArray(), limit, UnreadCap, ct);

        var peers = rows.ToDictionary(r => r.Channel, r => PeerOf(r.Channel, userId));
        var ids = peers.Values.Where(p => p is not null).Select(p => p!)
            .Concat(rows.Where(r => r.Last.SenderId is not null).Select(r => r.Last.SenderId!))
            .Distinct().ToArray();
        var profiles = ids.Length == 0
            ? new Dictionary<string, PublicProfile>()
            : (await _users.GetPublicAsync(ids, ct)).ToDictionary(p => p.Id);

        var views = new List<ConversationView>(rows.Count);
        foreach (var r in rows)
        {
            var senderName = r.Last.SenderId is not null && profiles.TryGetValue(r.Last.SenderId, out var sender) ? sender.Username : null;
            var last = new ChatMessageView(r.Last.Id, r.Channel, r.Last.SenderId, senderName, r.Last.Text, r.Last.CreatedUtc);

            if (groups.TryGetValue(r.Channel, out var group))
            {
                views.Add(new ConversationView(r.Channel, "group", null, group.Id, group.Name, last, r.LastReadId, r.Unread));
                continue;
            }

            var peer = peers[r.Channel];
            if (peer is null)
                continue;
            var title = profiles.TryGetValue(peer, out var p)
                ? (string.IsNullOrEmpty(p.DisplayName) ? p.Username : p.DisplayName)
                : "Player";
            views.Add(new ConversationView(r.Channel, "direct", peer, null, title, last, r.LastReadId, r.Unread));
        }

        return views;
    }

    /// <summary>
    /// Marks a direct or clan conversation as read up to <paramref name="messageId"/>. The mark only moves forward.
    /// Party chat and rooms are not stored, so they have no read mark.
    /// </summary>
    public async Task MarkReadAsync(string userId, string? channel, long messageId, CancellationToken ct)
    {
        if (messageId < 1)
            throw new GameException("invalid_request", "messageId must be a stored message id.");

        var target = ParseChannel(userId, channel);
        switch (target.Kind)
        {
            case ChannelKind.Direct:
                break; // the channel name already proves the player is one of the two
            case ChannelKind.Group:
                var role = await _groups.GetRoleAsync(target.Target!, userId, ct);
                if (role is null || role.Value > GroupRole.Member)
                    throw new GameException("not_a_member", "You are not a member of this group.");
                break;
            default:
                throw new GameException("not_stored", "Only direct and clan conversations keep a read mark.");
        }

        await _store.MarkReadAsync(userId, target.Name, messageId, ct);
    }

    /// <summary>The other player of a stored direct channel "dm:&lt;a&gt;:&lt;b&gt;", or null.</summary>
    private static string? PeerOf(string channel, string userId)
    {
        if (!channel.StartsWith("dm:", StringComparison.Ordinal))
            return null;
        var parts = channel[3..].Split(':');
        return parts.Length != 2 ? null : parts[0] == userId ? parts[1] : parts[1] == userId ? parts[0] : null;
    }

    // ---- what modules do (IChat) ----------------------------------------------------------------------------

    public async Task SendSystemAsync(string channel, string text, CancellationToken ct = default)
    {
        var target = ParseChannel("", channel);
        if (target.Kind == ChannelKind.Direct)
            throw new GameException("invalid_channel", "System messages cannot go to direct channels.");

        await DeliverAsync(target, null, null, CleanText(text), ct);
    }

    public async Task<bool> PushAsync(string userId, string type, object? data, CancellationToken ct = default)
    {
        if (!type.StartsWith("game.", StringComparison.Ordinal))
            throw new ArgumentException("Custom event types must start with \"game.\".", nameof(type));

        if (!_manager.IsOnline(userId))
            return false;

        await _manager.SendToUserAsync(userId, Frames.Build(type, null, data), ct);
        return true;
    }

    // ---- admin (called by /admin/api; no player permission checks) ------------------------------------------

    private static readonly Regex AdminChannelRx = new(@"^(room|dm|group|party):[A-Za-z0-9:_\-]{1,140}\z", RegexOptions.Compiled);

    public Task<IReadOnlyList<ChannelInfo>> AdminChannelsAsync(int limit, CancellationToken ct) =>
        _store.ListChannelsAsync(limit, ct);

    /// <summary>Stored messages of any channel. For direct messages pass the stored name <c>dm:&lt;idA&gt;:&lt;idB&gt;</c>.</summary>
    public Task<IReadOnlyList<ChatMessageView>> AdminHistoryAsync(string? channel, int limit, long? before, CancellationToken ct)
    {
        if (channel is null || !AdminChannelRx.IsMatch(channel))
            throw new GameException("invalid_channel", "Invalid channel name.");
        return ViewRowsAsync(channel, limit, before, ct);
    }

    public async Task<bool> AdminDeleteMessageAsync(long id, CancellationToken ct)
    {
        var channel = await _store.DeleteMessageAsync(id, ct);
        if (channel is null)
            return false;

        var frame = Frames.Build("channel.message_deleted", null, new { channel, id });
        if (channel.StartsWith("dm:", StringComparison.Ordinal))
            await _manager.SendToUsersAsync(channel[3..].Split(':'), frame, ct);
        else
            await _manager.SendToChannelAsync(channel, frame, ct);
        return true;
    }

    public async Task MuteAsync(string userId, int? minutes, string? reason, CancellationToken ct)
    {
        var user = await _users.GetAsync(userId, ct) ?? throw new GameException("not_found", "Player not found.");
        DateTime? until = minutes is > 0 ? _time.UtcNow().AddMinutes(Math.Min(minutes.Value, 60 * 24 * 365)) : null;
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];

        await _store.SetMuteAsync(user.Id, until, cleanReason, ct);
        _mutes.TryRemove(user.Id, out _);
    }

    public async Task<bool> UnmuteAsync(string userId, CancellationToken ct)
    {
        var removed = await _store.ClearMuteAsync(userId, ct);
        _mutes.TryRemove(userId, out _);
        return removed;
    }

    public Task<IReadOnlyList<MuteInfo>> MutesAsync(int limit, CancellationToken ct) => _store.ListMutesAsync(limit, ct);

    // ---- internals ----------------------------------------------------------------------------------------

    private async Task EnsureNotMutedAsync(string userId, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (!_mutes.TryGetValue(userId, out var state) || now - state.CheckedMs > MuteCacheMs)
        {
            state = new MuteState(now, await _store.GetMuteAsync(userId, ct));
            _mutes[userId] = state;
        }

        if (state.Mute is { } mute && (mute.UntilUtc is null || mute.UntilUtc > _time.UtcNow()))
        {
            var until = mute.UntilUtc is null ? "until an admin lifts it" : $"until {mute.UntilUtc:u}";
            throw new GameException("muted", $"You are muted {until}.");
        }
    }

    private async Task<IReadOnlyList<ChatMessageView>> ViewRowsAsync(string channelName, int limit, long? before, CancellationToken ct)
    {
        var rows = await _store.ListAsync(channelName, limit, before, ct);

        var senderIds = rows.Where(r => r.SenderId is not null).Select(r => r.SenderId!).Distinct().ToArray();
        var names = senderIds.Length == 0
            ? new Dictionary<string, string>()
            : (await _users.GetPublicAsync(senderIds, ct)).ToDictionary(p => p.Id, p => p.Username);

        return rows
            .Select(r => new ChatMessageView(
                r.Id, channelName, r.SenderId,
                r.SenderId is not null && names.TryGetValue(r.SenderId, out var name) ? name : null,
                r.Text, r.CreatedUtc))
            .ToList();
    }

    private async Task<ChatMessageView> DeliverAsync(
        ChannelRef target, string? senderId, string? senderName, string text, CancellationToken ct)
    {
        long? id = null;
        var createdUtc = _time.UtcNow();

        if (IsStored(target.Kind))
        {
            var row = await _store.AddAsync(target.Name, senderId, text, ct);
            id = row.Id;
            createdUtc = row.CreatedUtc;

            // What you wrote yourself is read.
            if (senderId is not null && target.Kind is ChannelKind.Direct or ChannelKind.Group)
                await _store.MarkReadAsync(senderId, target.Name, row.Id, ct);
            CleanupSometimes();
        }

        var message = new ChatMessageView(id, target.Name, senderId, senderName, text, createdUtc);
        var frame = Frames.Build("channel.message", null, message);

        if (target.Kind == ChannelKind.Direct)
            await _manager.SendToUsersAsync(new[] { target.UserA!, target.UserB! }, frame, ct);
        else
            await _manager.SendToChannelAsync(target.Name, frame, ct);

        return message;
    }

    private bool IsStored(ChannelKind kind) => kind switch
    {
        ChannelKind.Direct or ChannelKind.Group => true,
        ChannelKind.Room => _options.PersistRooms,
        _ => false
    };

    /// <summary>Every few hundred stored messages, delete the ones past the retention time in all channels.</summary>
    private void CleanupSometimes()
    {
        if (_options.RetentionDays <= 0 || Interlocked.Increment(ref _storedSinceCleanup) % 500 != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _store.DeleteOlderThanAsync(_options.RetentionDays);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Deleting old chat messages failed.");
            }
        });
    }

    private bool AllowSend(string userId)
    {
        var window = _rates.GetOrAdd(userId, _ => new RateWindow());
        lock (window)
        {
            var now = Environment.TickCount64;
            if (now - window.StartMs >= _options.WindowSeconds * 1000L)
            {
                window.StartMs = now;
                window.Count = 0;
            }

            if (window.Count >= _options.MessagesPerWindow)
                return false;

            window.Count++;
            return true;
        }
    }

    private string CleanText(string? text)
    {
        var clean = (text ?? "").Trim();
        if (clean.Length == 0)
            throw new GameException("invalid_message", "The message is empty.");
        if (clean.Length > _options.MaxTextLength)
            throw new GameException("invalid_message", $"A message can be at most {_options.MaxTextLength} characters.");
        if (!TextRules.IsClean(clean, allowNewlines: true))
            throw new GameException("invalid_message", "The message contains invalid characters.");
        return clean;
    }

    private static ChannelRef ParseChannel(string userId, string? input)
    {
        var text = (input ?? "").Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0)
            throw new GameException("invalid_channel", "A channel looks like room:<name>, dm:<userId>, group:<id> or party:<id>.");

        var kind = text[..colon];
        var rest = text[(colon + 1)..];

        switch (kind)
        {
            case "room" when RoomRx.IsMatch(rest):
                return new ChannelRef(ChannelKind.Room, "room:" + rest);

            case "group" when IdRx.IsMatch(rest):
                return new ChannelRef(ChannelKind.Group, "group:" + rest, rest);

            case "party" when IdRx.IsMatch(rest):
                return new ChannelRef(ChannelKind.Party, "party:" + rest, rest);

            case "dm":
            {
                // Either "dm:<other>" or the canonical "dm:<a>:<b>" that the server uses.
                var parts = rest.Split(':');
                string other;
                if (parts.Length == 1)
                    other = parts[0];
                else if (parts.Length == 2 && (parts[0] == userId || parts[1] == userId))
                    other = parts[0] == userId ? parts[1] : parts[0];
                else
                    break;

                if (!IdRx.IsMatch(other))
                    break;
                if (other == userId)
                    throw new GameException("invalid_channel", "You cannot message yourself.");

                var (first, second) = string.CompareOrdinal(userId, other) < 0 ? (userId, other) : (other, userId);
                return new ChannelRef(ChannelKind.Direct, $"dm:{first}:{second}", other, first, second);
            }
        }

        throw new GameException("invalid_channel", "A channel looks like room:<name>, dm:<userId>, group:<id> or party:<id>.");
    }

    private async Task AuthorizeAsync(ChannelRef target, string userId, CancellationToken ct)
    {
        switch (target.Kind)
        {
            case ChannelKind.Direct:
            {
                var other = await _users.GetAsync(target.Target!, ct);
                if (other is null || other.Disabled)
                    throw new GameException("not_found", "Player not found.");
                if (await _friends.IsBlockedBetweenAsync(userId, other.Id, ct))
                    throw new GameException("blocked", "You cannot message this player.");
                if (_options.DirectMessagesFriendsOnly && !await _friends.AreFriendsAsync(userId, other.Id, ct))
                    throw new GameException("not_friends", "You can only message your friends.");
                break;
            }

            case ChannelKind.Group:
            {
                var role = await _groups.GetRoleAsync(target.Target!, userId, ct);
                if (role is null || role.Value > GroupRole.Member)
                    throw new GameException("not_a_member", "You are not a member of this group.");
                break;
            }

            case ChannelKind.Party:
                if (!_parties.IsMember(target.Target!, userId))
                    throw new GameException("not_a_member", "You are not in this party.");
                break;
        }
    }
}

namespace GameServer.Core.Realtime;

/// <summary>Bound from "GameServer:Chat".</summary>
public sealed class ChatOptions
{
    /// <summary>Keep messages of public rooms. Direct and group messages are always kept (see RetentionDays); party chat never is.</summary>
    public bool PersistRooms { get; set; }

    /// <summary>Stored messages older than this are deleted. 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 30;

    public int MaxTextLength { get; set; } = 1000;

    /// <summary>A player may send this many messages per <see cref="WindowSeconds"/>.</summary>
    public int MessagesPerWindow { get; set; } = 10;

    public int WindowSeconds { get; set; } = 10;

    /// <summary>When true, only friends can send each other direct messages.</summary>
    public bool DirectMessagesFriendsOnly { get; set; }
}

/// <summary>A chat message as clients see it. <see cref="Id"/> is null for messages that are not stored (party chat, unstored rooms).</summary>
public sealed record ChatMessageView(
    long? Id, string Channel, string? SenderId, string? SenderName, string Text, DateTime CreatedUtc);

/// <summary>Request of the <c>chat.join</c> hook (and the <c>channel.join</c> / <c>channel.leave</c> messages).</summary>
public sealed record ChannelRequest(string? Channel);

/// <summary>Request of the <c>chat.send</c> hook (and the <c>channel.send</c> message). A before hook may change <c>Text</c>.</summary>
public sealed record ChatSendRequest(string? Channel, string? Text);

public sealed record ChatHistoryRequest(string? Channel, int? Limit, long? Before);

public sealed record ChatRow(long Id, string? SenderId, string Text, DateTime CreatedUtc);

/// <summary>A player who may not write in chat. <c>UntilUtc</c> null = until an admin lifts it.</summary>
public sealed record MuteInfo(string UserId, DateTime? UntilUtc, string? Reason, DateTime CreatedUtc);

public sealed record ChannelInfo(string Channel, long Messages, DateTime LastUtc);

/// <summary>A stored conversation of a player as the store sees it: the newest message and how far the player read.</summary>
public sealed record ConversationRow(string Channel, ChatRow Last, long LastReadId, int Unread);

/// <summary>
/// One of the player's conversations: a direct chat (<see cref="PeerId"/>) or a clan (<see cref="GroupId"/>).
/// <see cref="Channel"/> is the name the server puts on its messages (<c>dm:&lt;a&gt;:&lt;b&gt;</c>, <c>group:&lt;id&gt;</c>).
/// <see cref="Unread"/> counts messages from others after <see cref="LastReadId"/>, at most <see cref="ChatService.UnreadCap"/>.
/// </summary>
public sealed record ConversationView(
    string Channel, string Kind, string? PeerId, string? GroupId, string Title, ChatMessageView LastMessage, long LastReadId, int Unread);

public sealed record ChatReadRequest(string? Channel, long MessageId);

public interface IChatStore
{
    Task SetMuteAsync(string userId, DateTime? untilUtc, string? reason, CancellationToken ct = default);

    Task<bool> ClearMuteAsync(string userId, CancellationToken ct = default);

    /// <summary>The mute of a player if it is still active, otherwise null.</summary>
    Task<MuteInfo?> GetMuteAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<MuteInfo>> ListMutesAsync(int limit, CancellationToken ct = default);

    /// <summary>Deletes one message. Returns its channel, or null if it did not exist.</summary>
    Task<string?> DeleteMessageAsync(long id, CancellationToken ct = default);

    /// <summary>Channels that have stored messages, most recently active first.</summary>
    Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(int limit, CancellationToken ct = default);

    Task<ChatRow> AddAsync(string channel, string? senderId, string text, CancellationToken ct = default);

    /// <summary>Newest first. <paramref name="beforeId"/> continues after a previous page.</summary>
    Task<IReadOnlyList<ChatRow>> ListAsync(string channel, int limit, long? beforeId, CancellationToken ct = default);

    Task DeleteOlderThanAsync(string channel, int days, CancellationToken ct = default);

    Task DeleteOlderThanAsync(int days, CancellationToken ct = default);

    /// <summary>
    /// The player's stored conversations that have messages: every direct channel with them in it, plus the given group
    /// channels. Newest first, each with its last message, the read mark and the unread count (at most <paramref name="unreadCap"/>).
    /// </summary>
    Task<IReadOnlyList<ConversationRow>> ConversationsAsync(
        string userId, IReadOnlyCollection<string> groupChannels, int limit, int unreadCap, CancellationToken ct = default);

    /// <summary>Moves the player's read mark of a channel forward to <paramref name="messageId"/> (never back).</summary>
    Task MarkReadAsync(string userId, string channel, long messageId, CancellationToken ct = default);
}

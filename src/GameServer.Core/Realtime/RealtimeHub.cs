using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core.Matches;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Core.Realtime;

/// <summary>
/// The one place that understands client messages. It only sees <see cref="IConnection"/>, so every transport
/// (websocket, tcp, ...) behaves the same.
/// <para>Client → server: <c>auth</c> {token}, <c>ping</c>, <c>channel.join</c> {channel}, <c>channel.leave</c> {channel},
/// <c>channel.send</c> {channel,text}, <c>channel.history</c> {channel,limit,before}, <c>matchmaker.add</c> {mode,properties,partyId},
/// <c>matchmaker.remove</c> {ticketId}, <c>match.create</c> {mode,label,parameters}, <c>match.join</c> {matchId}, <c>match.list</c> {mode,limit}.
/// Server → client events: <c>matchmaker.matched</c>, <c>matchmaker.expired</c>.</para>
/// </summary>
public sealed class RealtimeHub : IConnectionHandler
{
    private sealed class State
    {
        public required IConnection Connection { get; init; }
        public DateTime ConnectedUtc { get; init; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public bool Authenticating { get; set; }
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly ConnectionManager _manager;
    private readonly ChatService _chat;
    private readonly TokenService _tokens;
    private readonly IUserStore _users;
    private readonly HookRunner _hooks;
    private readonly MatchActions _match;
    private readonly RealtimeOptions _options;
    private readonly ILogger<RealtimeHub> _log;

    public RealtimeHub(
        ConnectionManager manager, ChatService chat, TokenService tokens, IUserStore users,
        HookRunner hooks, MatchActions match, IOptions<RealtimeOptions> options, ILogger<RealtimeHub> log)
    {
        _manager = manager;
        _chat = chat;
        _tokens = tokens;
        _users = users;
        _hooks = hooks;
        _match = match;
        _options = options.Value;
        _log = log;
    }

    public Task OnConnectedAsync(IConnection connection)
    {
        _states[connection.Id] = new State { Connection = connection };
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(IConnection connection)
    {
        _states.TryRemove(connection.Id, out _);
        _manager.Unregister(connection);
        return Task.CompletedTask;
    }

    public async Task OnMessageAsync(IConnection connection, string message, CancellationToken ct)
    {
        if (!_states.TryGetValue(connection.Id, out var state))
            return;

        state.LastSeenUtc = DateTime.UtcNow;

        string type;
        string? id;
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                throw new JsonException("missing type");

            type = t.GetString()!;
            id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
        }
        catch (JsonException)
        {
            await ReplyErrorAsync(connection, null, "invalid_request", "Messages must be JSON like {\"type\":\"...\",\"id\":\"...\",\"data\":{}}.", ct);
            return;
        }

        try
        {
            if (type == "ping")
            {
                await _manager.SendAsync(connection, Frames.Build("pong", id), ct);
                return;
            }

            if (type == "auth")
            {
                await AuthenticateAsync(state, id, data, ct);
                return;
            }

            if (state.UserId is null)
            {
                await ReplyErrorAsync(connection, id, "not_authenticated", "Send an auth message first.", ct);
                return;
            }

            await HandleAsync(state, type, id, data, ct);
        }
        catch (GameException ex)
        {
            await ReplyErrorAsync(connection, id, ex.Code, ex.Message, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Realtime message {Type} failed.", type);
            await ReplyErrorAsync(connection, id, "internal_error", "Something went wrong.", ct);
        }
    }

    /// <summary>Closes connections that never authenticated or went quiet. Called every few seconds by the hosted service.</summary>
    public async Task SweepAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var state in _states.Values)
        {
            var authExpired = state.UserId is null && !state.Authenticating
                && (now - state.ConnectedUtc).TotalSeconds > _options.AuthTimeoutSeconds;
            var idle = _options.IdleTimeoutSeconds > 0
                && (now - state.LastSeenUtc).TotalSeconds > _options.IdleTimeoutSeconds;

            if (!authExpired && !idle)
                continue;

            try
            {
                await state.Connection.CloseAsync(authExpired ? "auth_timeout" : "idle_timeout");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Closing a timed-out connection failed.");
            }
        }
    }

    // ---- handlers -----------------------------------------------------------------------------------------

    private async Task AuthenticateAsync(State state, string? id, JsonElement data, CancellationToken ct)
    {
        var connection = state.Connection;
        if (state.UserId is not null)
        {
            await ReplyErrorAsync(connection, id, "already_authenticated", "This connection is already authenticated.", ct);
            return;
        }

        state.Authenticating = true;
        try
        {
            var token = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;

            var userId = token is null ? null : await _tokens.ValidateAsync(token);
            var user = userId is null ? null : await _users.GetAsync(userId, ct);

            if (user is null || user.Disabled)
            {
                await ReplyErrorAsync(connection, id, "unauthorized", "Invalid or expired token.", ct);
                await connection.CloseAsync("unauthorized");
                return;
            }

            if (_manager.CountForUser(user.Id) >= _options.MaxConnectionsPerUser)
            {
                await ReplyErrorAsync(connection, id, "too_many_connections", "Too many open connections for this account.", ct);
                await connection.CloseAsync("too_many_connections");
                return;
            }

            state.UserId = user.Id;
            state.UserName = user.Username;
            _manager.Register(user.Id, connection);

            await _manager.SendAsync(connection,
                Frames.Build("auth.ok", id, new { userId = user.Id, username = user.Username, transport = connection.TransportName }), ct);
        }
        finally
        {
            state.Authenticating = false;
        }
    }

    private async Task HandleAsync(State state, string type, string? id, JsonElement data, CancellationToken ct)
    {
        var connection = state.Connection;
        var userId = state.UserId!;

        switch (type)
        {
            case "channel.join":
            {
                var req = Read<ChannelRequest>(data);
                var channel = await _hooks.RunAsync(HookOps.ChatJoin, userId, req,
                    async r => new ChannelRequest(await _chat.JoinAsync(connection, userId, r.Channel, ct)), ct);
                await _manager.SendAsync(connection, Frames.Build("channel.joined", id, new { channel = channel.Channel }), ct);
                break;
            }

            case "channel.leave":
            {
                var req = Read<ChannelRequest>(data);
                var channel = _chat.Leave(connection, userId, req.Channel);
                await _manager.SendAsync(connection, Frames.Build("channel.left", id, new { channel }), ct);
                break;
            }

            case "channel.send":
            {
                var req = Read<ChatSendRequest>(data);
                var sent = await _hooks.RunAsync(HookOps.ChatSend, userId, req,
                    r => _chat.SendAsync(userId, state.UserName!, r.Channel, r.Text, ct), ct);
                await _manager.SendAsync(connection, Frames.Build("channel.sent", id, sent), ct);
                break;
            }

            case "channel.history":
            {
                var req = Read<ChatHistoryRequest>(data);
                var limit = Math.Clamp(req.Limit ?? 50, 1, 100);
                var items = await _chat.HistoryAsync(userId, req.Channel, limit, req.Before, ct);
                await _manager.SendAsync(connection, Frames.Build("channel.history", id, new { items }), ct);
                break;
            }

            case "matchmaker.add":
            {
                var ticket = await _match.QueueAsync(userId, Read<MatchmakerRequest>(data), ct);
                await _manager.SendAsync(connection, Frames.Build("matchmaker.ticket", id, ticket), ct);
                break;
            }

            case "matchmaker.remove":
            {
                var removed = await _match.UnqueueAsync(userId, Read<TicketRequest>(data).TicketId);
                await _manager.SendAsync(connection, Frames.Build("matchmaker.removed", id, new { removed }), ct);
                break;
            }

            case "match.create":
            {
                var created = await _match.CreateAsync(userId, Read<CreateMatchRequest>(data), ct);
                await _manager.SendAsync(connection, Frames.Build("match.created", id, created), ct);
                break;
            }

            case "match.join":
            {
                var connect = await _match.JoinAsync(userId, Read<MatchRequest>(data), ct);
                await _manager.SendAsync(connection, Frames.Build("match.joined", id, connect), ct);
                break;
            }

            case "match.list":
            {
                var req = data.ValueKind == JsonValueKind.Object ? Read<MatchListRequest>(data) : new MatchListRequest();
                await _manager.SendAsync(connection, Frames.Build("match.list", id, new { items = _match.List(req.Mode, req.Limit) }), ct);
                break;
            }

            default:
                await ReplyErrorAsync(connection, id, "unknown_type", $"Unknown message type \"{type}\".", ct);
                break;
        }
    }

    private static T Read<T>(JsonElement data) where T : class
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new GameException("invalid_request", "The message needs a data object.");

        try
        {
            return data.Deserialize<T>(Frames.Json) ?? throw new GameException("invalid_request", "The message data is invalid.");
        }
        catch (JsonException)
        {
            throw new GameException("invalid_request", "The message data is invalid.");
        }
    }

    private Task ReplyErrorAsync(IConnection connection, string? id, string code, string message, CancellationToken ct) =>
        _manager.SendAsync(connection, Frames.Build("error", id, new { code, message }), ct);
}

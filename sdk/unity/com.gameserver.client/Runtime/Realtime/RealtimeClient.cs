using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    /// <summary>One message from the server: a reply to a request (has an id) or a pushed event.</summary>
    public sealed class RealtimeMessage
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public JToken Data { get; set; }
    }

    /// <summary>The event "notification.new": a new inbox message arrived (read it with <see cref="NotificationsApi"/>).</summary>
    public sealed class NotificationEvent
    {
        public long Id { get; set; }
        public bool Broadcast { get; set; }
        public string Subject { get; set; }
        public bool HasReward { get; set; }
    }

    public sealed class PartyInvite
    {
        public string PartyId { get; set; }
        /// <summary>Who invited you (shape as sent by the server).</summary>
        public JToken From { get; set; }
        public DateTime? ExpiresUtc { get; set; }
    }

    public sealed class PartyLeft
    {
        public string PartyId { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// The live connection: chat, matchmaker and pushed events (friend requests, party invites, notifications...).
    /// All events are raised on the thread that created the <see cref="GameClient"/> (the Unity main thread).
    /// </summary>
    public sealed class RealtimeClient
    {
        private sealed class Pending
        {
            public IRealtimeConnection Connection;
            public TaskCompletionSource<RealtimeMessage> Completion;
        }

        private readonly ApiClient _api;
        private readonly GameClientOptions _options;
        private readonly Dispatcher _dispatch;
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, Pending> _pending = new ConcurrentDictionary<string, Pending>();
        private readonly ConcurrentDictionary<string, bool> _channels = new ConcurrentDictionary<string, bool>();
        private readonly Dictionary<string, List<Action<JToken>>> _handlers = new Dictionary<string, List<Action<JToken>>>();
        private IRealtimeConnection _connection;
        private CancellationTokenSource _pingCts;
        private JObject _serverInfo;
        private string _transport;
        private volatile bool _wanted;
        private long _idCounter;

        internal RealtimeClient(ApiClient api, GameClientOptions options, Dispatcher dispatch)
        {
            _api = api;
            _options = options;
            _dispatch = dispatch;
        }

        public bool IsConnected => _connection != null;

        /// <summary>The protocol in use, for example "websocket".</summary>
        public string TransportName => _connection == null ? null : _transport;

        // ---- events -------------------------------------------------------------------------------------------

        public event Action Connected;
        /// <summary>The connection ended; the argument is the reason. With AutoReconnect the client tries again by itself.</summary>
        public event Action<string> Disconnected;
        /// <summary>Connected again after a drop (channels were joined again).</summary>
        public event Action Reconnected;

        public event Action<ChatMessage> ChatMessageReceived;
        /// <summary>(channel, message id)</summary>
        public event Action<string, long> ChatMessageDeleted;
        public event Action<MatchFound> MatchFound;
        public event Action<TicketExpired> MatchmakerExpired;
        public event Action<NotificationEvent> NotificationReceived;
        public event Action<PartyInvite> PartyInvited;
        public event Action<Party> PartyUpdated;
        public event Action<PartyLeft> PartyLeft;

        /// <summary>Every pushed event, including ones without a typed event here (friend.request, group.invite, goal.completed, game.*).</summary>
        public event Action<RealtimeMessage> EventReceived;

        /// <summary>Listens to one event type, e.g. <c>On("friend.request", data =&gt; ...)</c>. Handlers run on the main thread.</summary>
        public void On(string type, Action<JToken> handler)
        {
            lock (_handlers)
            {
                if (!_handlers.TryGetValue(type, out var list))
                    _handlers[type] = list = new List<Action<JToken>>();
                list.Add(handler);
            }
        }

        public void Off(string type, Action<JToken> handler)
        {
            lock (_handlers)
            {
                if (_handlers.TryGetValue(type, out var list))
                    list.Remove(handler);
            }
        }

        // ---- connecting ---------------------------------------------------------------------------------------

        /// <summary>
        /// Opens the connection with the configured protocol and signs in with the player's token.
        /// Log in first. Pass <paramref name="transport"/> to override <see cref="GameClientOptions.RealtimeTransport"/>.
        /// </summary>
        public async Task ConnectAsync(string transport = null, CancellationToken ct = default(CancellationToken))
        {
            await _connectLock.WaitAsync(ct);
            try
            {
                if (_connection != null)
                    return;

                _wanted = true;
                await OpenAsync(transport ?? _options.RealtimeTransport, ct);
            }
            finally
            {
                _connectLock.Release();
            }

            _dispatch.Post(() => Connected?.Invoke());
        }

        /// <summary>Closes the connection and stops reconnecting.</summary>
        public async Task DisconnectAsync()
        {
            _wanted = false;
            _channels.Clear();
            var connection = _connection;
            if (connection != null)
                await connection.CloseAsync();
        }

        private async Task<JObject> ServerInfoAsync(CancellationToken ct)
        {
            if (_serverInfo == null)
                _serverInfo = await _api.SendAsync<JObject>("GET", "/v1/realtime", null, false, ct);
            return _serverInfo;
        }

        private async Task OpenAsync(string transportName, CancellationToken ct)
        {
            var transport = _options.RealtimeTransports.FirstOrDefault(t => t.Name == transportName);
            if (transport == null)
                throw new ApiException("transport_not_supported", "This client has no realtime transport named \"" + transportName + "\".", 0);

            var info = await ServerInfoAsync(ct);
            var description = (info["transports"] as JArray)?.OfType<JObject>().FirstOrDefault(t => (string)t["name"] == transportName);
            if (description == null)
                throw new ApiException("transport_not_enabled", "The server does not offer the \"" + transportName + "\" realtime transport.", 503);

            var connection = await transport.ConnectAsync(new RealtimeEndpoint
            {
                ApiUri = new Uri(_api.BaseUrl),
                Info = description,
                MaxMessageBytes = info["maxMessageBytes"] == null ? 16 * 1024 : (int)info["maxMessageBytes"]
            }, ct);

            connection.MessageReceived += OnMessage;
            connection.Closed += reason => OnClosed(connection, reason);
            connection.Start();

            try
            {
                var token = await _api.FreshTokenAsync(ct);
                await RequestOnAsync(connection, "auth", new { token }, ct);
            }
            catch (Exception)
            {
                try { await connection.CloseAsync(); } catch (Exception) { }
                throw;
            }

            _transport = transportName;
            _connection = connection;
            StartPings(connection);
        }

        // ---- requests -----------------------------------------------------------------------------------------

        /// <summary>
        /// Sends a message and waits for the server's reply. A reply of type "error" is thrown as <see cref="ApiException"/>.
        /// Most callers use the typed methods below; this is for message types you add on the server.
        /// </summary>
        public Task<RealtimeMessage> RequestAsync(string type, object data = null, CancellationToken ct = default(CancellationToken))
        {
            var connection = _connection;
            if (connection == null)
                throw new ApiException("not_connected", "The realtime connection is not open. Call ConnectAsync first.", 0);
            return RequestOnAsync(connection, type, data, ct);
        }

        private async Task<RealtimeMessage> RequestOnAsync(IRealtimeConnection connection, string type, object data, CancellationToken ct)
        {
            var id = "c" + Interlocked.Increment(ref _idCounter);
            var pending = new Pending
            {
                Connection = connection,
                Completion = new TaskCompletionSource<RealtimeMessage>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            _pending[id] = pending;

            try
            {
                var envelope = new JObject { ["type"] = type, ["id"] = id };
                if (data != null)
                    envelope["data"] = JToken.FromObject(data, Json.Serializer);

                await connection.SendAsync(envelope.ToString(Formatting.None), ct);

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(_options.RealtimeRequestTimeoutSeconds));
                    using (timeout.Token.Register(() => pending.Completion.TrySetCanceled()))
                        return await pending.Completion.Task;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ApiException("request_timeout", "The server did not answer \"" + type + "\" in time.", 0);
            }
            finally
            {
                Pending ignored;
                _pending.TryRemove(id, out ignored);
            }
        }

        private async Task<T> RequestAsync<T>(string type, object data, string expectedReply, Func<JToken, T> read, CancellationToken ct)
        {
            var reply = await RequestAsync(type, data, ct);
            if (expectedReply != null && reply.Type != expectedReply)
                throw new ApiException("unexpected_reply", "Expected \"" + expectedReply + "\" but got \"" + reply.Type + "\".", 0);
            return read(reply.Data);
        }

        // ---- chat ---------------------------------------------------------------------------------------------

        /// <summary>Joins a channel to receive its messages (see <see cref="Channels"/>). Rejoined automatically after a reconnect.</summary>
        public async Task<string> JoinChannelAsync(string channel, CancellationToken ct = default(CancellationToken))
        {
            var joined = await RequestAsync("channel.join", new { channel }, "channel.joined", d => (string)d["channel"], ct);
            _channels[joined ?? channel] = true;
            return joined;
        }

        public async Task LeaveChannelAsync(string channel, CancellationToken ct = default(CancellationToken))
        {
            var left = await RequestAsync("channel.leave", new { channel }, "channel.left", d => (string)d["channel"], ct);
            bool ignored;
            _channels.TryRemove(left ?? channel, out ignored);
            _channels.TryRemove(channel, out ignored);
        }

        /// <summary>Sends a message. Rooms and groups need a prior <see cref="JoinChannelAsync"/>; direct messages do not.</summary>
        public Task<ChatMessage> SendMessageAsync(string channel, string text, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("channel.send", new { channel, text }, "channel.sent", d => d.To<ChatMessage>(), ct);

        public Task<List<ChatMessage>> ChannelHistoryAsync(string channel, int limit = 50, long? before = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("channel.history", new { channel, limit, before }, "channel.history", d => d["items"].To<List<ChatMessage>>(), ct);

        // ---- matchmaker and matches ---------------------------------------------------------------------------

        /// <summary>
        /// Joins the matchmaking queue of a mode. When enough players are found you get <see cref="MatchFound"/>.
        /// <paramref name="properties"/> are the rule values the mode asks for (for example region or skill).
        /// Pass <paramref name="partyId"/> to queue your whole party.
        /// </summary>
        public Task<Ticket> AddToMatchmakerAsync(string mode, Dictionary<string, object> properties = null, string partyId = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("matchmaker.add", new { mode, properties, partyId }, "matchmaker.ticket", d => d.To<Ticket>(), ct);

        public Task<bool> RemoveFromMatchmakerAsync(string ticketId, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("matchmaker.remove", new { ticketId }, "matchmaker.removed", d => (bool)d["removed"], ct);

        /// <summary>Creates a match yourself (only for modes that allow it) and returns how to connect.</summary>
        public Task<MatchCreated> CreateMatchAsync(string mode, string label = null, string parameters = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("match.create", new { mode, label, parameters }, "match.created", d => d.To<MatchCreated>(), ct);

        public Task<MatchConnectInfo> JoinMatchAsync(string matchId, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("match.join", new { matchId }, "match.joined", d => d.To<MatchConnectInfo>(), ct);

        public Task<List<MatchInfo>> ListMatchesAsync(string mode = null, int? limit = null, CancellationToken ct = default(CancellationToken)) =>
            RequestAsync("match.list", new { mode, limit }, "match.list", d => d["items"].To<List<MatchInfo>>(), ct);

        // ---- incoming messages --------------------------------------------------------------------------------

        private void OnMessage(string text)
        {
            RealtimeMessage message;
            try
            {
                var obj = JsonConvert.DeserializeObject<JObject>(text, Json.Settings);
                message = new RealtimeMessage
                {
                    Type = (string)obj["type"],
                    Id = obj["id"] == null || obj["id"].Type == JTokenType.Null ? null : (string)obj["id"],
                    Data = obj["data"]
                };
            }
            catch (Exception)
            {
                return;
            }

            if (message.Id != null && _pending.TryGetValue(message.Id, out var pending))
            {
                if (message.Type == "error")
                {
                    var code = message.Data == null ? "error" : (string)message.Data["code"] ?? "error";
                    var detail = message.Data == null ? "" : (string)message.Data["message"] ?? "";
                    pending.Completion.TrySetException(new ApiException(code, detail, 400));
                }
                else
                {
                    pending.Completion.TrySetResult(message);
                }

                return;
            }

            _dispatch.Post(() => Raise(message));
        }

        private void Raise(RealtimeMessage message)
        {
            var data = message.Data;
            switch (message.Type)
            {
                case "channel.message":
                    ChatMessageReceived?.Invoke(data.To<ChatMessage>());
                    break;
                case "channel.message_deleted":
                    ChatMessageDeleted?.Invoke((string)data["channel"], (long)data["id"]);
                    break;
                case "matchmaker.matched":
                    MatchFound?.Invoke(data.To<MatchFound>());
                    break;
                case "matchmaker.expired":
                    MatchmakerExpired?.Invoke(data.To<TicketExpired>());
                    break;
                case "notification.new":
                    NotificationReceived?.Invoke(data.To<NotificationEvent>());
                    break;
                case "party.invite":
                    PartyInvited?.Invoke(data.To<PartyInvite>());
                    break;
                case "party.update":
                    PartyUpdated?.Invoke(data.To<Party>());
                    break;
                case "party.left":
                    PartyLeft?.Invoke(data.To<PartyLeft>());
                    break;
            }

            EventReceived?.Invoke(message);

            Action<JToken>[] listeners;
            lock (_handlers)
                listeners = _handlers.TryGetValue(message.Type ?? "", out var list) ? list.ToArray() : new Action<JToken>[0];

            foreach (var listener in listeners)
            {
                try
                {
                    listener(data);
                }
                catch (Exception ex)
                {
                    _options.Log?.Invoke("A realtime handler threw: " + ex);
                }
            }
        }

        // ---- keeping the connection ---------------------------------------------------------------------------

        private void StartPings(IRealtimeConnection connection)
        {
            StopPings();
            var cts = new CancellationTokenSource();
            _pingCts = cts;
            var seconds = Math.Max(_options.PingIntervalSeconds, 5);

            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                        await RequestOnAsync(connection, "ping", null, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // no pong: treat the connection as dead so the reconnect logic takes over
                        try { await connection.CloseAsync(); } catch (Exception) { }
                        return;
                    }
                }
            });
        }

        private void StopPings()
        {
            var cts = _pingCts;
            _pingCts = null;
            if (cts != null)
                cts.Cancel();
        }

        private void OnClosed(IRealtimeConnection connection, string reason)
        {
            foreach (var entry in _pending.ToArray())
            {
                if (ReferenceEquals(entry.Value.Connection, connection))
                    entry.Value.Completion.TrySetException(new ApiException("connection_closed", "The connection closed: " + reason, 0));
            }

            if (!ReferenceEquals(connection, _connection))
                return;

            _connection = null;
            StopPings();
            _dispatch.Post(() => Disconnected?.Invoke(reason));

            if (_wanted && _options.AutoReconnect)
                Task.Run(ReconnectLoop);
        }

        private async Task ReconnectLoop()
        {
            var delaySeconds = 1;
            while (_wanted)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (!_wanted)
                    return;

                try
                {
                    await _connectLock.WaitAsync();
                    try
                    {
                        if (_connection != null)
                            return;
                        await OpenAsync(_transport ?? _options.RealtimeTransport, CancellationToken.None);
                    }
                    finally
                    {
                        _connectLock.Release();
                    }

                    foreach (var channel in _channels.Keys.ToArray())
                    {
                        try
                        {
                            await RequestAsync("channel.join", new { channel }, CancellationToken.None);
                        }
                        catch (ApiException)
                        {
                            bool ignored;
                            _channels.TryRemove(channel, out ignored);
                        }
                    }

                    _dispatch.Post(() => Reconnected?.Invoke());
                    return;
                }
                catch (ApiException ex) when (ex.Code == "unauthorized" || ex.Code == "not_logged_in" || ex.Code == "invalid_refresh_token" || ex.Code == "transport_not_enabled")
                {
                    _wanted = false;
                    _dispatch.Post(() => Disconnected?.Invoke(ex.Code));
                    return;
                }
                catch (Exception)
                {
                    delaySeconds = Math.Min(delaySeconds * 2, 30);
                }
            }
        }
    }
}

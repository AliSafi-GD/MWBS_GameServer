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
    /// <summary>
    /// The live connection: chat, matchmaker and pushed events (friend requests, party invites, notifications...).
    /// All events are raised on the thread that created the <see cref="GameClient"/> (the Unity main thread).
    /// </summary>
    public sealed partial class RealtimeClient : IRealtimeClient
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
    }
}

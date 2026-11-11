using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // =============================================================================================================
    // Match transports. Each game mode names its protocol on the server ("websocket", "udp", ...). The info you get
    // from the matchmaker (MatchFound) or from JoinMatch says which one, and MatchesApi.ConnectAsync picks the
    // matching client below. To add a protocol, implement IMatchTransportClient and add it to
    // GameClientOptions.MatchTransports.
    // =============================================================================================================

    /// <summary>How the server should send a message. Only UDP honours Unreliable and Sequenced; other transports are always reliable.</summary>
    public enum MatchDelivery
    {
        /// <summary>Arrives, in order.</summary>
        Reliable,
        /// <summary>May get lost or arrive out of order. Cheapest; good for positions sent every tick.</summary>
        Unreliable,
        /// <summary>May get lost, but an older packet never arrives after a newer one.</summary>
        Sequenced
    }

    public interface IMatchTransportConnection
    {
        event Action<byte[]> DataReceived;
        event Action<string> Closed;
        void Start();
        Task SendAsync(byte[] data, MatchDelivery delivery, CancellationToken ct);
        Task CloseAsync();
    }

    public interface IMatchTransportClient
    {
        /// <summary>The server's name for it: "websocket", "udp", ...</summary>
        string Name { get; }

        Task<IMatchTransportConnection> ConnectAsync(MatchConnectInfo info, Uri apiUri, CancellationToken ct);
    }

    /// <summary>WebSocket match connection: ws://host/match?token=... with one binary frame per message.</summary>
    public sealed class WebSocketMatchTransportClient : IMatchTransportClient
    {
        public string Name => "websocket";

        public async Task<IMatchTransportConnection> ConnectAsync(MatchConnectInfo info, Uri apiUri, CancellationToken ct)
        {
            var endpoint = info.Endpoint;
            var path = endpoint == null || endpoint["path"] == null ? "/match" : (string)endpoint["path"];
            var tokenName = endpoint == null || endpoint["tokenQuery"] == null ? "token" : (string)endpoint["tokenQuery"];
            var uri = Endpoints.WebSocketUri(apiUri, path, tokenName + "=" + Uri.EscapeDataString(info.Token));

            var link = await WebSocketLink.ConnectAsync(uri, 4 * 1024 * 1024, ct);
            return new Connection(link);
        }

        private sealed class Connection : IMatchTransportConnection
        {
            private readonly WebSocketLink _link;

            public Connection(WebSocketLink link)
            {
                _link = link;
                _link.Received += (data, text) => DataReceived?.Invoke(data);
                _link.Closed += reason => Closed?.Invoke(reason);
            }

            public event Action<byte[]> DataReceived;
            public event Action<string> Closed;

            public void Start() => _link.Start();

            public Task SendAsync(byte[] data, MatchDelivery delivery, CancellationToken ct) => _link.SendAsync(data, false, ct);

            public Task CloseAsync() => _link.CloseAsync();
        }
    }

    /// <summary>
    /// An open connection to one match. Events are raised on the thread that created the <see cref="GameClient"/>.
    /// Message contents are up to the match handler on the server (the built-in "relay" handler forwards your bytes
    /// to the other players, prefixed with the sender's slot; see <see cref="RelayMessage"/>).
    /// </summary>
    public sealed class MatchConnection
    {
        private readonly IMatchTransportConnection _inner;
        private readonly Dispatcher _dispatch;
        private int _open = 1;

        public MatchConnectInfo Info { get; }
        public string Transport => Info.Transport;
        public bool IsOpen => Volatile.Read(ref _open) == 1;

        /// <summary>A message from the server (or relayed from another player).</summary>
        public event Action<byte[]> DataReceived;

        /// <summary>The connection ended; the argument is the reason (the server ends it when the match is over or you are kicked).</summary>
        public event Action<string> Closed;

        internal MatchConnection(IMatchTransportConnection inner, MatchConnectInfo info, Dispatcher dispatch)
        {
            _inner = inner;
            _dispatch = dispatch;
            Info = info;
            _inner.DataReceived += data => _dispatch.Post(() => DataReceived?.Invoke(data));
            _inner.Closed += reason =>
            {
                Interlocked.Exchange(ref _open, 0);
                _dispatch.Post(() => Closed?.Invoke(reason));
            };
        }

        internal void Start() => _inner.Start();

        public Task SendAsync(byte[] data, MatchDelivery delivery = MatchDelivery.Reliable, CancellationToken ct = default(CancellationToken)) =>
            _inner.SendAsync(data, delivery, ct);

        public Task SendTextAsync(string text, MatchDelivery delivery = MatchDelivery.Reliable, CancellationToken ct = default(CancellationToken)) =>
            _inner.SendAsync(Encoding.UTF8.GetBytes(text), delivery, ct);

        public Task SendJsonAsync(object value, MatchDelivery delivery = MatchDelivery.Reliable, CancellationToken ct = default(CancellationToken)) =>
            SendTextAsync(Json.Serialize(value), delivery, ct);

        public Task CloseAsync() => _inner.CloseAsync();
    }

    /// <summary>Reads messages of the server's built-in "relay" match handler: one byte (the sender's slot), then the sender's bytes.</summary>
    public static class RelayMessage
    {
        public static bool TryParse(byte[] data, out int slot, out byte[] payload)
        {
            if (data == null || data.Length < 1)
            {
                slot = -1;
                payload = null;
                return false;
            }

            slot = data[0];
            payload = new byte[data.Length - 1];
            Buffer.BlockCopy(data, 1, payload, 0, payload.Length);
            return true;
        }
    }

    /// <summary>
    /// Matchmaker, finding and creating matches over HTTP, and connecting to a match. The same matchmaker actions
    /// exist on <see cref="RealtimeClient"/>; use either. Queueing needs an open realtime connection because the result
    /// ("matchmaker.matched") arrives there.
    /// </summary>
    public sealed class MatchesApi
    {
        private readonly ApiClient _api;
        private readonly GameClientOptions _options;
        private readonly Dispatcher _dispatch;

        internal MatchesApi(ApiClient api, GameClientOptions options, Dispatcher dispatch)
        {
            _api = api;
            _options = options;
            _dispatch = dispatch;
        }

        /// <summary>The server's match modes and which protocol each one uses.</summary>
        public Task<MatchModes> ModesAsync(CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<MatchModes>("GET", "/v1/match/modes", null, false, ct);

        public async Task<Ticket> QueueAsync(string mode, Dictionary<string, object> properties = null, string partyId = null, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/matchmaker/tickets", new { mode, properties, partyId }, true, ct);
            return root.Get<Ticket>("ticket");
        }

        public async Task<List<Ticket>> TicketsAsync(CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/matchmaker/tickets", ct);
            return root.Get<List<Ticket>>("tickets");
        }

        public async Task<bool> UnqueueAsync(string ticketId, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                await _api.SendAsync("DELETE", "/v1/matchmaker/tickets/" + ApiClient.Seg(ticketId), null, true, ct);
                return true;
            }
            catch (ApiException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        /// <summary>Creates a match (only for modes that allow it) and returns how to connect to it.</summary>
        public Task<MatchCreated> CreateAsync(string mode, string label = null, string parameters = null, CancellationToken ct = default(CancellationToken)) =>
            _api.SendAsync<MatchCreated>("POST", "/v1/matches", new { mode, label, parameters }, true, ct);

        public async Task<List<MatchInfo>> ListAsync(string mode = null, int? limit = null, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync(ApiClient.Query("/v1/matches", "mode", mode, "limit", limit), ct);
            return root.Get<List<MatchInfo>>("matches");
        }

        public async Task<MatchInfo> GetAsync(string matchId, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.GetObjectAsync("/v1/matches/" + ApiClient.Seg(matchId), ct);
            return root.Get<MatchInfo>("match");
        }

        /// <summary>Reserves a place in an open match. Then call <see cref="ConnectAsync"/> within a few seconds.</summary>
        public async Task<MatchConnectInfo> JoinAsync(string matchId, CancellationToken ct = default(CancellationToken))
        {
            var root = await _api.SendAsync<JObject>("POST", "/v1/matches/" + ApiClient.Seg(matchId) + "/join", null, true, ct);
            return root.Get<MatchConnectInfo>("connect");
        }

        /// <summary>
        /// Connects to the match using the protocol named in <paramref name="info"/> (from <see cref="MatchFound.ToConnectInfo"/>,
        /// <see cref="JoinAsync"/> or a created match). The token is single use and short lived.
        /// </summary>
        public async Task<MatchConnection> ConnectAsync(MatchConnectInfo info, CancellationToken ct = default(CancellationToken))
        {
            var transport = _options.MatchTransports.FirstOrDefault(t => t.Name == info.Transport);
            if (transport == null)
            {
                throw new ApiException("transport_not_supported",
                    "This client cannot connect with \"" + info.Transport + "\". " +
                    (info.Transport == "udp" ? "Call UseLiteNetLib() on the client options (needs the LiteNetLib assembly)." : "Add a transport to GameClientOptions.MatchTransports."), 0);
            }

            var inner = await transport.ConnectAsync(info, new Uri(_api.BaseUrl), ct);
            var connection = new MatchConnection(inner, info, _dispatch);
            connection.Start();
            return connection;
        }

        public Task<MatchConnection> ConnectAsync(MatchFound found, CancellationToken ct = default(CancellationToken)) =>
            ConnectAsync(found.ToConnectInfo(), ct);
    }
}

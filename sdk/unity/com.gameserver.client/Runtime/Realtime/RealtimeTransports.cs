using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // =============================================================================================================
    // Realtime transports. The server decides which ones are on (GET /v1/realtime); the client uses the one named in
    // GameClientOptions.RealtimeTransport. Messages are always JSON text, whatever carries them.
    // To add a protocol, implement these two interfaces and add it to GameClientOptions.RealtimeTransports.
    // =============================================================================================================

    /// <summary>What a transport needs to reach the server.</summary>
    public sealed class RealtimeEndpoint
    {
        /// <summary>The server's HTTP address (host and scheme are reused by transports).</summary>
        public Uri ApiUri { get; set; }

        /// <summary>This transport's entry from GET /v1/realtime, e.g. { name: "websocket", path: "/ws" } or { name: "tcp", port: 7351, tls: false }.</summary>
        public JObject Info { get; set; }

        public int MaxMessageBytes { get; set; }
    }

    public interface IRealtimeConnection
    {
        event Action<string> MessageReceived;

        /// <summary>Raised once, with a reason, when the connection ends for any cause.</summary>
        event Action<string> Closed;

        /// <summary>Begins delivering messages. Called after the events are subscribed.</summary>
        void Start();

        Task SendAsync(string message, CancellationToken ct);
        Task CloseAsync();
    }

    public interface IRealtimeTransport
    {
        /// <summary>The server's name for it: "websocket", "tcp", ...</summary>
        string Name { get; }

        Task<IRealtimeConnection> ConnectAsync(RealtimeEndpoint endpoint, CancellationToken ct);
    }

    /// <summary>Builds the ws:// / wss:// address for the realtime and the match WebSocket.</summary>
    internal static class Endpoints
    {
        /// <summary>ws:// or wss:// on the same host as the API, with the given path and query.</summary>
        public static Uri WebSocketUri(Uri api, string path, string query = null)
        {
            var builder = new UriBuilder(api)
            {
                Scheme = api.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Path = path.StartsWith("/") ? path : "/" + path,
                Query = query ?? ""
            };
            return builder.Uri;
        }
    }
}

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
    public sealed class WebSocketRealtimeTransport : IRealtimeTransport
    {
        public string Name => "websocket";

        public async Task<IRealtimeConnection> ConnectAsync(RealtimeEndpoint endpoint, CancellationToken ct)
        {
            var path = (string)endpoint.Info["path"] ?? "/ws";
            var uri = Endpoints.WebSocketUri(endpoint.ApiUri, path);
            var link = await WebSocketLink.ConnectAsync(uri, Math.Max(endpoint.MaxMessageBytes * 4, 1024 * 1024), ct);
            return new Connection(link);
        }

        private sealed class Connection : IRealtimeConnection
        {
            private readonly WebSocketLink _link;

            public Connection(WebSocketLink link)
            {
                _link = link;
                _link.Received += (data, text) => MessageReceived?.Invoke(Encoding.UTF8.GetString(data));
                _link.Closed += reason => Closed?.Invoke(reason);
            }

            public event Action<string> MessageReceived;
            public event Action<string> Closed;

            public void Start() => _link.Start();

            public Task SendAsync(string message, CancellationToken ct) => _link.SendAsync(Encoding.UTF8.GetBytes(message), true, ct);

            public Task CloseAsync() => _link.CloseAsync();
        }
    }
}

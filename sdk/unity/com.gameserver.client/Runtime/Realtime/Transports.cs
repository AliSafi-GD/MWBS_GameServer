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

    // ---- WebSocket (shared by realtime and match) -----------------------------------------------------------------

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

    internal sealed class WebSocketLink
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly int _maxBytes;
        private int _closed;

        public event Action<byte[], bool> Received;
        public event Action<string> Closed;

        private WebSocketLink(int maxBytes)
        {
            _maxBytes = maxBytes;
        }

        public static async Task<WebSocketLink> ConnectAsync(Uri uri, int maxBytes, CancellationToken ct)
        {
            var link = new WebSocketLink(maxBytes);
            try
            {
                await link._socket.ConnectAsync(uri, ct);
            }
            catch (OperationCanceledException)
            {
                link._socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                link._socket.Dispose();
                throw new ApiException("network_error", ex.Message, 0);
            }

            return link;
        }

        public void Start()
        {
            Task.Run(ReceiveLoop);
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[8192];
            var message = new MemoryStream();
            var reason = "closed";
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        reason = result.CloseStatusDescription ?? "closed_by_server";
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > _maxBytes)
                    {
                        reason = "message_too_large";
                        break;
                    }

                    if (!result.EndOfMessage)
                        continue;

                    var data = message.ToArray();
                    message.SetLength(0);
                    try
                    {
                        Received?.Invoke(data, result.MessageType == WebSocketMessageType.Text);
                    }
                    catch (Exception)
                    {
                        // a faulty handler must not stop the connection
                    }
                }
            }
            catch (OperationCanceledException)
            {
                reason = "closed_by_client";
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            RaiseClosed(reason);
        }

        public async Task SendAsync(byte[] data, bool text, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(data), text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ApiException("network_error", ex.Message, 0);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            }
            catch (Exception)
            {
                // closing a broken socket is best effort
            }

            _cts.Cancel();
            RaiseClosed("closed_by_client");
        }

        private void RaiseClosed(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            try { _socket.Dispose(); } catch (Exception) { }
            try { Closed?.Invoke(reason); } catch (Exception) { }
        }
    }

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

    // ---- TCP ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Raw TCP (optionally TLS). Each message is a 4-byte big-endian length followed by UTF-8 JSON, exactly the same
    /// messages as on the websocket. Not available on WebGL.
    /// </summary>
    public sealed class TcpRealtimeTransport : IRealtimeTransport
    {
        public string Name => "tcp";

        public async Task<IRealtimeConnection> ConnectAsync(RealtimeEndpoint endpoint, CancellationToken ct)
        {
            var port = endpoint.Info["port"] == null ? 7351 : (int)endpoint.Info["port"];
            var tls = endpoint.Info["tls"] != null && (bool)endpoint.Info["tls"];
            var host = (string)endpoint.Info["host"];
            if (string.IsNullOrEmpty(host))
                host = endpoint.ApiUri.Host;

            var client = new TcpClient { NoDelay = true };
            try
            {
                var connect = client.ConnectAsync(host, port);
                var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(10), ct));
                if (finished != connect)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new ApiException("network_error", "Connecting to " + host + ":" + port + " timed out.", 0);
                }

                await connect;

                Stream stream = client.GetStream();
                if (tls)
                {
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsClientAsync(host);
                    stream = ssl;
                }

                return new Connection(client, stream, Math.Max(endpoint.MaxMessageBytes * 4, 1024 * 1024));
            }
            catch (ApiException)
            {
                client.Close();
                throw;
            }
            catch (OperationCanceledException)
            {
                client.Close();
                throw;
            }
            catch (Exception ex)
            {
                client.Close();
                throw new ApiException("network_error", ex.Message, 0);
            }
        }

        private sealed class Connection : IRealtimeConnection
        {
            private readonly TcpClient _client;
            private readonly Stream _stream;
            private readonly int _maxBytes;
            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
            private int _closed;

            public Connection(TcpClient client, Stream stream, int maxBytes)
            {
                _client = client;
                _stream = stream;
                _maxBytes = maxBytes;
            }

            public event Action<string> MessageReceived;
            public event Action<string> Closed;

            public void Start()
            {
                Task.Run(ReceiveLoop);
            }

            private async Task ReceiveLoop()
            {
                var header = new byte[4];
                var reason = "closed";
                try
                {
                    while (true)
                    {
                        if (!await ReadExactAsync(header, 4))
                        {
                            reason = "closed_by_server";
                            break;
                        }

                        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                        if (length <= 0 || length > _maxBytes)
                        {
                            reason = "invalid_frame_length";
                            break;
                        }

                        var payload = new byte[length];
                        if (!await ReadExactAsync(payload, length))
                        {
                            reason = "closed_by_server";
                            break;
                        }

                        try
                        {
                            MessageReceived?.Invoke(Encoding.UTF8.GetString(payload));
                        }
                        catch (Exception)
                        {
                            // a faulty handler must not stop the connection
                        }
                    }
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                }

                RaiseClosed(reason);
            }

            private async Task<bool> ReadExactAsync(byte[] buffer, int count)
            {
                var read = 0;
                while (read < count)
                {
                    var n = await _stream.ReadAsync(buffer, read, count - read);
                    if (n == 0)
                        return false;
                    read += n;
                }

                return true;
            }

            public async Task SendAsync(string message, CancellationToken ct)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                var frame = new byte[4 + bytes.Length];
                frame[0] = (byte)(bytes.Length >> 24);
                frame[1] = (byte)(bytes.Length >> 16);
                frame[2] = (byte)(bytes.Length >> 8);
                frame[3] = (byte)bytes.Length;
                Buffer.BlockCopy(bytes, 0, frame, 4, bytes.Length);

                await _sendLock.WaitAsync(ct);
                try
                {
                    await _stream.WriteAsync(frame, 0, frame.Length, ct);
                    await _stream.FlushAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ApiException("network_error", ex.Message, 0);
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            public Task CloseAsync()
            {
                RaiseClosed("closed_by_client");
                return Task.FromResult(0);
            }

            private void RaiseClosed(string reason)
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                    return;

                try { _stream.Dispose(); } catch (Exception) { }
                try { _client.Close(); } catch (Exception) { }
                try { Closed?.Invoke(reason); } catch (Exception) { }
            }
        }
    }
}

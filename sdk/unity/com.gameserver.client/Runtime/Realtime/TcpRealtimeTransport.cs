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

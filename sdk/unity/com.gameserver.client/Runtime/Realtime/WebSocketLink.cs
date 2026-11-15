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
}

using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using GameServer.Core.Realtime;

namespace GameServer.Transports.Ws;

/// <summary>
/// ws:// and wss:// (WebSocket over TLS is the same transport; TLS is provided by Kestrel or your reverse proxy).
/// One JSON message per text frame. Clients sign in with an "auth" message right after connecting.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    private readonly RealtimeOptions _options;
    private readonly WsRealtimeOptions _ws;
    private readonly IConnectionHandler _handler;
    private readonly ILogger<WebSocketTransport> _log;

    public WebSocketTransport(IOptions<RealtimeOptions> options, IConfiguration config, IConnectionHandler handler, ILogger<WebSocketTransport> log)
    {
        _options = options.Value;
        _ws = config.GetSection("GameServer:Realtime:WebSocket").Get<WsRealtimeOptions>() ?? new WsRealtimeOptions();
        _handler = handler;
        _log = log;
    }

    public string Name => "websocket";

    public object Describe() => new { name = Name, path = _ws.Path };

    public void ConfigureApp(IApplicationBuilder app) =>
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RequestDelegate handle = HandleAsync;
        endpoints.Map(_ws.Path, handle);
    }

    private async Task HandleAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("WebSocket connection expected.");
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var connection = new WebSocketConnection(socket, http.Connection.RemoteIpAddress?.ToString());

        await _handler.OnConnectedAsync(connection);
        try
        {
            await ReceiveLoopAsync(socket, connection, http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WebSocket connection {Id} ended with an error.", connection.Id);
        }
        finally
        {
            await _handler.OnDisconnectedAsync(connection);
            await connection.CloseAsync("closed");
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, IConnection connection, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (message.Length + result.Count > _options.MaxMessageBytes)
                {
                    await connection.CloseAsync("message_too_large");
                    return;
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            await _handler.OnMessageAsync(connection, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), ct);
        }
    }

    private sealed class WebSocketConnection : IConnection
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public WebSocketConnection(WebSocket socket, string? remoteAddress)
        {
            _socket = socket;
            RemoteAddress = remoteAddress;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string TransportName => "websocket";

        public string? RemoteAddress { get; }

        public async Task SendAsync(string message, CancellationToken ct = default)
        {
            var bytes = Encoding.UTF8.GetBytes(message);

            await _sendLock.WaitAsync(ct);
            try
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(string reason)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
            }
            catch
            {
                // Already closed or broken: nothing left to do.
            }
            finally
            {
                // Ends a receive loop that would otherwise wait for a client that does not answer the close.
                try { _socket.Abort(); } catch { }
            }
        }
    }
}

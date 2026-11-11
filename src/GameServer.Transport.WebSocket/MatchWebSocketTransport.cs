using System.Net.WebSockets;
using GameServer.Core.Matches;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GameServer.Transports.Ws;

/// <summary>Match messages over WebSocket: <c>ws://host/match?token=...</c>, one binary frame per message.</summary>
public sealed class WebSocketMatchTransport : IMatchTransport
{
    private readonly MatchOptions _options;
    private readonly WsMatchOptions _ws;
    private readonly IMatchHost _host;
    private readonly ILogger<WebSocketMatchTransport> _log;

    public WebSocketMatchTransport(IOptions<MatchOptions> options, IConfiguration config, IMatchHost host, ILogger<WebSocketMatchTransport> log)
    {
        _options = options.Value;
        _ws = config.GetSection("GameServer:Match:WebSocket").Get<WsMatchOptions>() ?? new WsMatchOptions();
        _host = host;
        _log = log;
    }

    public string Name => "websocket";

    public object Describe() => new { name = Name, path = _ws.Path, tokenQuery = "token" };

    public void ConfigureApp(IApplicationBuilder app) =>
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

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

        var token = http.Request.Query["token"].ToString();
        if (!_host.IsTokenValid(token))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var connection = new Connection(socket, http.Connection.RemoteIpAddress?.ToString());

        if (!await _host.OnConnectedAsync(connection, token))
        {
            await connection.CloseAsync("join_refused");
            return;
        }

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
            _log.LogDebug(ex, "Match WebSocket {Id} ended with an error.", connection.Id);
        }
        finally
        {
            await _host.OnDisconnectedAsync(connection);
            await connection.CloseAsync("closed");
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, Connection connection, CancellationToken ct)
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

            await _host.OnMessageAsync(connection, message.GetBuffer().AsMemory(0, (int)message.Length));
        }
    }

    private sealed class Connection : IMatchConnection
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Connection(WebSocket socket, string? remoteAddress)
        {
            _socket = socket;
            RemoteAddress = remoteAddress;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string TransportName => "websocket";
        public string? RemoteAddress { get; }

        public async Task SendAsync(ReadOnlyMemory<byte> data, GameServer.Abstractions.MatchDelivery delivery)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.SendAsync(data, WebSocketMessageType.Binary, true, timeout.Token);
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
            }
            finally
            {
                try { _socket.Abort(); } catch { }
            }
        }
    }
}

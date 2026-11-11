using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Core.Realtime;

/// <summary>
/// One client connection, whatever protocol carries it. Messages are UTF-8 JSON text; the transport only moves them
/// (a WebSocket text frame, a length-prefixed TCP frame, ...). Everything above this interface — authentication,
/// chat, parties — is shared by all transports.
/// </summary>
public interface IConnection
{
    string Id { get; }

    /// <summary>The name of the transport that carries this connection, e.g. "websocket".</summary>
    string TransportName { get; }

    string? RemoteAddress { get; }

    /// <summary>Sends one message. Must be safe to call from several threads at once.</summary>
    Task SendAsync(string message, CancellationToken ct = default);

    /// <summary>Closes the connection. The transport then reports it through <see cref="IConnectionHandler.OnDisconnectedAsync"/>.</summary>
    Task CloseAsync(string reason);
}

/// <summary>What a transport calls for every connection. Implemented by the realtime hub.</summary>
public interface IConnectionHandler
{
    Task OnConnectedAsync(IConnection connection);

    /// <summary>Called for one message at a time per connection; the next message is read after this returns.</summary>
    Task OnMessageAsync(IConnection connection, string message, CancellationToken ct);

    /// <summary>Always called once when a connection that was announced with OnConnectedAsync ends.</summary>
    Task OnDisconnectedAsync(IConnection connection);
}

/// <summary>
/// A way for clients to reach the realtime features. To add a protocol, implement this interface and register it
/// (see <see cref="RealtimeBuilder.Add{T}"/>); then it can be switched on by name in config or in code.
/// </summary>
public interface ITransport
{
    /// <summary>Short lower-case name, e.g. "websocket" or "tcp".</summary>
    string Name { get; }

    /// <summary>What clients need to know to connect; returned by GET /v1/realtime.</summary>
    object Describe() => new { name = Name };

    /// <summary>Add middleware before endpoints run (the WebSocket transport enables WebSockets here).</summary>
    void ConfigureApp(IApplicationBuilder app) { }

    /// <summary>Map HTTP endpoints, for transports that live inside ASP.NET Core (WebSocket).</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    /// <summary>
    /// Runs for the whole life of the server, for transports that own a socket (TCP). Return when stopping;
    /// transports with nothing to run can leave the default.
    /// </summary>
    Task RunAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

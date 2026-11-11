using GameServer.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Core.Matches;

/// <summary>
/// One player's connection to a match, whatever protocol carries it. Messages are raw bytes; what is inside is
/// agreed between the game client and the match handler. Everything above this interface (tokens, handlers, ticks)
/// is shared by all match transports.
/// </summary>
public interface IMatchConnection
{
    string Id { get; }

    /// <summary>Name of the transport that carries this connection, e.g. "websocket" or "udp".</summary>
    string TransportName { get; }

    string? RemoteAddress { get; }

    /// <summary>Sends one message. Must be safe to call from several threads. Transports without separate delivery modes send everything reliably.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, MatchDelivery delivery);

    /// <summary>Closes the connection. The transport then reports it through <see cref="IMatchHost.OnDisconnectedAsync"/>.</summary>
    Task CloseAsync(string reason);
}

/// <summary>What a match transport calls. Implemented by the match service.</summary>
public interface IMatchHost
{
    /// <summary>
    /// Quick check of a join token that does not use it up. For transports that must decide whether to accept a
    /// connection immediately (UDP handshake). Must not block.
    /// </summary>
    bool IsTokenValid(string token);

    /// <summary>
    /// A connection presented a join token. Returns false when it was refused, in which case the transport must
    /// close the connection. Call <see cref="OnDisconnectedAsync"/> only for connections that returned true.
    /// </summary>
    Task<bool> OnConnectedAsync(IMatchConnection connection, string token);

    Task OnMessageAsync(IMatchConnection connection, ReadOnlyMemory<byte> data);

    /// <summary>Call once when an accepted connection ends, for any reason.</summary>
    Task OnDisconnectedAsync(IMatchConnection connection);
}

/// <summary>
/// A way for players to reach a match. To add a protocol (UDP with LiteNetLib, QUIC...), implement this interface and
/// register it with <see cref="MatchTransportBuilder.Add{T}"/>; then switch it on by name in config or code, and
/// choose it per mode with <c>"transport": "name"</c> in the "matches" config.
/// </summary>
public interface IMatchTransport
{
    /// <summary>Short lower-case name, e.g. "websocket" or "udp".</summary>
    string Name { get; }

    /// <summary>What clients need to find this transport (path, port...). Sent to clients with the join token.</summary>
    object Describe() => new { name = Name };

    /// <summary>Add middleware before endpoints run, for transports that live inside ASP.NET Core.</summary>
    void ConfigureApp(IApplicationBuilder app) { }

    /// <summary>Map HTTP endpoints, for transports that live inside ASP.NET Core.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    /// <summary>Runs for the whole life of the server, for transports that own a socket. Return when <paramref name="stoppingToken"/> is cancelled.</summary>
    Task RunAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

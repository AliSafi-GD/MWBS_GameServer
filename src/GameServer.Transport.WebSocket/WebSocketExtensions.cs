using GameServer.Core.Matches;
using GameServer.Core.Realtime;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Transports.Ws;

public static class WebSocketExtensions
{
    /// <summary>Makes the "websocket" transport available for chat/realtime (ws:// and wss://, JSON text frames).</summary>
    public static RealtimeBuilder AddWebSocket(this RealtimeBuilder builder) =>
        builder.Add("websocket", sp => ActivatorUtilities.CreateInstance<WebSocketTransport>(sp));

    /// <summary>Makes the "websocket" transport available for matches (binary frames, join token in the query string).</summary>
    public static MatchTransportBuilder AddWebSocket(this MatchTransportBuilder builder) =>
        builder.Add("websocket", sp => ActivatorUtilities.CreateInstance<WebSocketMatchTransport>(sp));
}

namespace GameServer.Transports.Ws;

/// <summary>Bound from "GameServer:Realtime:WebSocket".</summary>
public sealed class WsRealtimeOptions
{
    /// <summary>
    /// Path of the endpoint. It serves ws:// or wss:// depending on whether the request came in over TLS, which is
    /// decided by Kestrel or by your reverse proxy, not by this setting.
    /// </summary>
    public string Path { get; set; } = "/ws";
}

/// <summary>Bound from "GameServer:Match:WebSocket".</summary>
public sealed class WsMatchOptions
{
    /// <summary>Path of the endpoint: ws://host/match?token=...</summary>
    public string Path { get; set; } = "/match";
}

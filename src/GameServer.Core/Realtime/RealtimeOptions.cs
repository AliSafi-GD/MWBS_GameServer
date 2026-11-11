namespace GameServer.Core.Realtime;

/// <summary>
/// Bound from "GameServer:Realtime" (env vars like GameServer__Realtime__Transports). Settings that belong to one
/// protocol (the WebSocket path, the TCP port...) are read by that transport's own plugin from its own sub-section.
/// </summary>
public sealed class RealtimeOptions
{
    /// <summary>
    /// Which protocols this server speaks, comma separated: "websocket", "tcp" or the name of a transport you added
    /// in code. At least one must be enabled. Code can override this with <see cref="RealtimeBuilder.Use"/>.
    /// </summary>
    public string Transports { get; set; } = "websocket";

    /// <summary>Largest message accepted from a client.</summary>
    public int MaxMessageBytes { get; set; } = 16 * 1024;

    /// <summary>A connection must send a valid "auth" message within this time.</summary>
    public int AuthTimeoutSeconds { get; set; } = 10;

    /// <summary>A signed-in connection that sends nothing (not even "ping") for this long is closed.</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;

    public int MaxConnectionsPerUser { get; set; } = 5;

    public IReadOnlyList<string> ParseTransports() =>
        (Transports ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.ToLowerInvariant())
            .Distinct()
            .ToArray();
}

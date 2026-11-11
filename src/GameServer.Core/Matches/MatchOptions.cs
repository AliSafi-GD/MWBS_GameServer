namespace GameServer.Core.Matches;

/// <summary>Bound from "GameServer:Match" (env vars like GameServer__Match__Transports).</summary>
public sealed class MatchOptions
{
    /// <summary>
    /// Which protocols matches can use, comma separated: "websocket", "udp" (after AddLiteNetLib) or the name of a
    /// transport you added in code. The first one is the default for modes that name no transport.
    /// </summary>
    public string Transports { get; set; } = "websocket";

    /// <summary>Largest message accepted from a match client.</summary>
    public int MaxMessageBytes { get; set; } = 16 * 1024;

    /// <summary>How long a join token is valid.</summary>
    public int TokenSeconds { get; set; } = 30;

    /// <summary>How often the matchmaker looks for matches.</summary>
    public int MatchmakerIntervalMs { get; set; } = 1000;

    /// <summary>A player may wait in this many matchmaker queues at the same time.</summary>
    public int MaxTicketsPerUser { get; set; } = 3;

    /// <summary>Largest number of matches running at once.</summary>
    public int MaxMatches { get; set; } = 10_000;

    public IReadOnlyList<string> ParseTransports() =>
        (Transports ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.ToLowerInvariant())
            .Distinct()
            .ToArray();
}

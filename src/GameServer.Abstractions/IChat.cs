namespace GameServer.Abstractions;

/// <summary>Realtime messaging from server code, independent of which transport a player is connected with.</summary>
public interface IChat
{
    /// <summary>
    /// Sends a message from the server (no sender) to a <c>room:</c>, <c>group:</c> or <c>party:</c> channel.
    /// Whether it is stored follows the channel type, like normal chat.
    /// </summary>
    Task SendSystemAsync(string channel, string text, CancellationToken ct = default);

    /// <summary>
    /// Pushes a custom event to one player if they are online and returns whether it was delivered.
    /// <paramref name="type"/> must start with "game." so it cannot be confused with the server's own events.
    /// </summary>
    Task<bool> PushAsync(string userId, string type, object? data, CancellationToken ct = default);
}

namespace GameServer.Abstractions;

/// <summary>What the player receives when they claim a notification. Amounts must be positive.</summary>
public sealed class NotificationReward
{
    public Dictionary<string, long>? Wallet { get; set; }
    public Dictionary<string, long>? Items { get; set; }
}

/// <summary>A player's inbox: messages, optionally with a reward they press "claim" on.</summary>
public interface INotifications
{
    /// <summary>Sends a notification to one player and returns its id. Expired notifications are hidden and cannot be claimed.</summary>
    Task<long> SendAsync(
        string userId, string subject, string? body = null, NotificationReward? reward = null,
        DateTime? expiresUtc = null, CancellationToken ct = default);
}

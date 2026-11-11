using GameServer.Abstractions;

namespace GameServer.Core;

public sealed record NotificationInfo(
    long Id, string Subject, string? Body, NotificationReward? Reward,
    DateTime CreatedUtc, DateTime? ExpiresUtc, bool Read, bool Claimed);

public sealed record NotificationClaim(NotificationReward Reward, EconomyState State);

/// <summary>The inbox as the host and admin panel see it (modules only get <see cref="INotifications"/>).</summary>
public interface INotificationStore : INotifications
{
    /// <summary>Newest first. Expired notifications are left out unless <paramref name="includeExpired"/> (admin).</summary>
    Task<IReadOnlyList<NotificationInfo>> ListAsync(
        string userId, int limit, bool unreadOnly, bool includeExpired = false, CancellationToken ct = default);

    Task<bool> MarkReadAsync(string userId, long id, CancellationToken ct = default);

    /// <summary>Grants the reward once. Errors: not_found, no_reward, already_claimed, expired.</summary>
    Task<NotificationClaim> ClaimAsync(string userId, long id, CancellationToken ct = default);

    /// <summary>Deletes a notification. A reward that was not claimed yet must be claimed first (reward_unclaimed).</summary>
    Task<bool> DeleteAsync(string userId, long id, CancellationToken ct = default);

    /// <summary>Sends the same notification to every active (not banned) player. Returns how many received it.</summary>
    Task<int> BroadcastAsync(string subject, string? body, NotificationReward? reward, DateTime? expiresUtc, CancellationToken ct = default);
}

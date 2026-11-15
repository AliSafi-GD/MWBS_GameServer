using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Admin;

/// <summary>Admin accounts (not the master key, see <see cref="AdminKeys.IsMaster"/>). Only a hash of each key is stored.</summary>
public interface IAdminAccountStore
{
    /// <summary>The enabled admin with this key, or null.</summary>
    Task<AdminActor?> AuthenticateAsync(string key, CancellationToken ct);

    Task<IReadOnlyList<AdminAccount>> ListAdminsAsync(CancellationToken ct);

    /// <summary>Creates an admin and returns the key (only shown this once). GameException "invalid_name" / "name_taken".</summary>
    Task<(AdminAccount Account, string Key)> CreateAdminAsync(string name, AdminRole role, string createdBy, CancellationToken ct);

    Task<bool> UpdateAdminAsync(string id, AdminRole? role, bool? disabled, CancellationToken ct);

    /// <summary>Replaces the key of an admin (for a lost key). Returns the new key, or null if the admin does not exist.</summary>
    Task<string?> ResetKeyAsync(string id, CancellationToken ct);

    Task<bool> DeleteAdminAsync(string id, CancellationToken ct);
}

/// <summary>What admins did (every admin API write and denied attempt).</summary>
public interface IAdminAuditLog
{
    /// <summary>Records a request; the body is masked with <see cref="AdminRules.RedactBody"/>.</summary>
    Task AuditAsync(AdminActor actor, string method, string path, string? body, int status, string? ip, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string? actor, string? text, long? before, int limit, CancellationToken ct);

    /// <summary>Deletes entries older than 180 days.</summary>
    Task PruneAsync(CancellationToken ct);
}

/// <summary>Earlier versions of config keys (the last 50 per key), for rollback.</summary>
public interface IConfigHistoryStore
{
    Task AddConfigHistoryAsync(ConfigEntry old, string changedBy, CancellationToken ct);

    Task<IReadOnlyList<ConfigHistoryEntry>> ConfigHistoryAsync(string key, int limit, CancellationToken ct);

    Task<ConfigHistoryEntry?> ConfigHistoryItemAsync(string key, long id, CancellationToken ct);
}

/// <summary>Admin views of one player: their storage records and a timeline of what happened to the account.</summary>
public interface IPlayerAdminData
{
    Task<IReadOnlyList<StorageRecord>> ListStorageAsync(string userId, CancellationToken ct);

    /// <summary>GameException "invalid_key" / "too_large" (<see cref="AdminRules.ValidateStorageWrite"/>), "not_found".</summary>
    Task WriteStorageAsync(string userId, string key, JsonElement value, CancellationToken ct);

    Task<bool> DeleteStorageAsync(string userId, string key, CancellationToken ct);

    Task<IReadOnlyList<TimelineEvent>> TimelineAsync(string userId, int limit, CancellationToken ct);
}

/// <summary>Live-ops notifications that are sent at a set time.</summary>
public interface IBroadcastStore
{
    /// <summary>GameException "invalid_subject" / "invalid_audience" (<see cref="AdminRules.ValidateBroadcast"/>).</summary>
    Task<ScheduledBroadcast> ScheduleAsync(
        DateTime runUtc, string subject, string? body, NotificationReward? reward, string audience, int? expiresInDays,
        string createdBy, CancellationToken ct);

    Task<ScheduledBroadcast?> GetBroadcastAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<ScheduledBroadcast>> ListBroadcastsAsync(int limit, CancellationToken ct);

    /// <summary>Claims due broadcasts (so two server instances never send the same one twice).</summary>
    Task<IReadOnlyList<ScheduledBroadcast>> ClaimDueAsync(CancellationToken ct);

    Task FinishBroadcastAsync(long id, int sent, string? error, CancellationToken ct);

    /// <summary>Cancels a pending broadcast, or removes a finished one. Returns false if it does not exist or is being sent.</summary>
    Task<bool> DeleteBroadcastAsync(long id, CancellationToken ct);
}

/// <summary>Numbers for the admin dashboard: a sample per minute (kept 7 days) and totals.</summary>
public interface IAdminMetrics
{
    Task SaveSampleAsync(int online, int matches, int queued, CancellationToken ct);

    /// <summary>Deletes samples older than 7 days.</summary>
    Task PruneAsync(CancellationToken ct);

    /// <summary>Samples of the last <paramref name="minutes"/> minutes, averaged into at most ~120 buckets.</summary>
    Task<IReadOnlyList<MetricPoint>> SeriesAsync(int minutes, CancellationToken ct);

    Task<DashboardTotals> TotalsAsync(CancellationToken ct);
}

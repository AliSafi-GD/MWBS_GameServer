using System.Text.Json;

namespace GameServer.Core;

/// <summary>A full account. Visible to the owner (without Disabled) and to admins.</summary>
public sealed record User(
    string Id,
    string Username,
    string DisplayName,
    string Avatar,
    string Lang,
    string Timezone,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? LastLoginUtc,
    bool Disabled);

/// <summary>What other players are allowed to see.</summary>
public sealed record PublicProfile(string Id, string Username, string DisplayName, string Avatar, DateTime CreatedUtc);

/// <summary>Fields to change; null means "leave as is".</summary>
public sealed record ProfileUpdate(string? Username, string? DisplayName, string? Avatar, string? Lang, string? Timezone);

public sealed record UserPage(IReadOnlyList<User> Items, int Total);

/// <summary>Device ids act like passwords, so they are only ever shown masked.</summary>
public sealed record DeviceInfo(string DeviceIdMasked, DateTime CreatedUtc);

/// <summary>A linked external login. Label is a human-readable hint (display name, masked phone), never a secret.</summary>
public sealed record IdentityInfo(string Provider, string Label, DateTime CreatedUtc);

public sealed record StorageEntry(string Key, JsonElement Value, DateTime UpdatedUtc);

public sealed record UserExport(
    User User,
    IReadOnlyList<DeviceInfo> Devices,
    IReadOnlyList<IdentityInfo> Identities,
    IReadOnlyList<StorageEntry> Storage);

public enum LinkResult
{
    Linked,
    AlreadyLinked,
    InUseByOtherAccount,
    ProviderAlreadyLinked
}

public enum UnlinkResult
{
    Unlinked,
    NotLinked,
    LastLoginMethod
}

public interface IUserStore
{
    // ---- device (guest) login -------------------------------------------------------------------------------

    /// <summary>
    /// Logs in with a device id. Creates the account on first use when <paramref name="create"/> is true
    /// (using <paramref name="username"/> if given, otherwise a generated one). Returns null if the device
    /// is unknown and create is false. Throws GameException("username_taken") if the requested name is in use.
    /// </summary>
    Task<User?> LoginDeviceAsync(string deviceId, bool create, string? username, CancellationToken ct = default);

    // ---- external logins (Google Play, phone, ...) ----------------------------------------------------------

    /// <summary>Returns the account that owns this login (and records the login time), or null.</summary>
    Task<User?> LoginWithIdentityAsync(string provider, string subject, CancellationToken ct = default);

    /// <summary>Creates a new account that is logged in through this identity.</summary>
    Task<User?> CreateWithIdentityAsync(string provider, string subject, string label, string? username, CancellationToken ct = default);

    /// <summary>Attaches a verified identity to an existing account.</summary>
    Task<LinkResult> LinkIdentityAsync(string userId, string provider, string subject, string label, CancellationToken ct = default);

    /// <summary>Removes a login from an account, but never the last remaining login method.</summary>
    Task<UnlinkResult> UnlinkIdentityAsync(string userId, string provider, CancellationToken ct = default);

    Task<IReadOnlyList<IdentityInfo>> ListIdentitiesAsync(string userId, CancellationToken ct = default);

    // ---- accounts -------------------------------------------------------------------------------------------

    Task<User?> GetAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<PublicProfile>> GetPublicAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);

    /// <summary>Applies an already-validated update. Returns null if the user does not exist.</summary>
    Task<User?> UpdateProfileAsync(string userId, ProfileUpdate update, CancellationToken ct = default);

    Task<bool> SetDisabledAsync(string userId, bool disabled, CancellationToken ct = default);

    /// <summary>Deletes the account and (via cascade) everything stored for it.</summary>
    Task<bool> DeleteAsync(string userId, CancellationToken ct = default);

    Task<UserPage> SearchAsync(string? query, int limit, int offset, CancellationToken ct = default);

    Task<UserExport?> ExportAsync(string userId, CancellationToken ct = default);
}

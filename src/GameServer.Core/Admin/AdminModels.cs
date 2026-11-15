using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Abstractions;

namespace GameServer.Core.Admin;

public enum AdminRole { ReadOnly = 0, Support = 1, Admin = 2 }

/// <summary>Who is calling the admin API. The master key from settings is "root".</summary>
public sealed record AdminActor(string Id, string Name, AdminRole Role);

public sealed record AdminAccount(string Id, string Name, string Role, DateTime CreatedUtc, string? CreatedBy, bool Disabled, DateTime? LastUsedUtc);

public sealed record AuditEntry(long Id, DateTime AtUtc, string Actor, string Role, string Method, string Path, string? Body, int Status, string? Ip);

public sealed record ConfigHistoryEntry(long Id, string Key, JsonElement? Value, bool IsPublic, string? ChangedBy, DateTime AtUtc);

public sealed record ScheduledBroadcast(
    long Id, DateTime RunUtc, string Subject, string? Body, NotificationReward? Reward, string Audience,
    int? ExpiresInDays, string Status, string? CreatedBy, DateTime CreatedUtc, DateTime? SentUtc, int? SentCount, string? Error);

public sealed record MetricPoint(DateTime AtUtc, int Online, int Matches, int Queued);

public sealed record StorageRecord(string Key, JsonElement Value, DateTime UpdatedUtc);

public sealed record TimelineEvent(DateTime AtUtc, string Type, string Title, string? Detail);

public sealed record DayCount(string Day, int Count);

public sealed record EconomyFlow(string Name, long Earned, long Spent);

public sealed record DashboardTotals(
    int Users, int Banned, int Signups24h, int Logins24h, int Signups7d, IReadOnlyList<EconomyFlow> Economy24h,
    IReadOnlyList<DayCount> SignupsPerDay);

/// <summary>Which role may call which admin route. Paths are relative to /admin/api.</summary>
public static class AdminPolicy
{
    private static readonly Regex[] SupportWrites =
    {
        new(@"^/users/[^/]+/(ban|unban|mute|disconnect|notifications)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^/chat/messages/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^/groups/[^/]+/members/[^/]+/(kick|ban)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    public static bool IsAllowed(AdminRole role, string method, string path)
    {
        if (role == AdminRole.Admin)
            return true;

        var read = method is "GET" or "HEAD";
        if (read)
            return !(path.StartsWith("/admins", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/audit", StringComparison.OrdinalIgnoreCase));

        if (role != AdminRole.Support)
            return false;

        if (method is not ("POST" or "DELETE"))
            return false;

        return SupportWrites.Any(r => r.IsMatch(path));
    }

    public static bool TryParseRole(string? text, out AdminRole role) =>
        Enum.TryParse(text, ignoreCase: true, out role) && Enum.IsDefined(role);
}

/// <summary>Admin keys: the master key from settings, generated keys and their stored hash.</summary>
public static class AdminKeys
{
    /// <summary>The actor of the master key (GameServer:Admin:Key).</summary>
    public static readonly AdminActor Root = new("root", "root", AdminRole.Admin);

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>True when <paramref name="given"/> is the configured master key (constant-time compare).</summary>
    public static bool IsMaster(string given, string masterKey) =>
        !string.IsNullOrEmpty(given) && !string.IsNullOrEmpty(masterKey)
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(masterKey)));

    /// <summary>A new random admin key ("gsk_" + 43 url-safe characters).</summary>
    public static string New() =>
        "gsk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Throws GameException "invalid_name" unless the name is 2-32 letters, digits, '_', '.' or '-' and not "root".</summary>
    public static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]{2,32}$") || name.Equals("root", StringComparison.OrdinalIgnoreCase))
            throw new GameException("invalid_name", "Name must be 2-32 letters, digits, '_', '.' or '-' (and not 'root').");
    }
}

/// <summary>Checks and clean-up rules of the admin data, kept apart from the database code so they can be tested.</summary>
public static class AdminRules
{
    private static readonly Regex Secret = new("(\"[^\"]*(?:key|secret|password|token|credential)[^\"]*\"\\s*:\\s*)\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A request body for the audit log: secrets masked, at most 2000 characters; null when empty.</summary>
    public static string? RedactBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var text = Secret.Replace(body, "$1\"***\"");
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }

    /// <summary>A path for the audit log: at most 500 characters.</summary>
    public static string AuditPath(string path) => path.Length > 500 ? path[..500] : path;

    /// <summary>Throws GameException "invalid_key" / "too_large" for an admin write to player storage.</summary>
    public static void ValidateStorageWrite(string key, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new GameException("invalid_key", "Storage key must be 1-128 characters.");
        if (rawValue.Length > 256 * 1024)
            throw new GameException("too_large", "Storage value is limited to 256 KB.");
    }

    /// <summary>Throws GameException "invalid_subject" / "invalid_audience" for a scheduled broadcast.</summary>
    public static void ValidateBroadcast(string subject, string audience)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
            throw new GameException("invalid_subject", "Subject is required (max 200 characters).");
        if (audience is not ("all" or "online"))
            throw new GameException("invalid_audience", "Audience must be 'all' or 'online'.");
    }

    /// <summary>A LIKE pattern that matches <paramref name="text"/> literally anywhere ('%' and '_' escaped).</summary>
    public static string ContainsPattern(string text) => "%" + EscapeLike(text) + "%";

    /// <summary>Escapes '%' and '_' for a LIKE pattern.</summary>
    public static string EscapeLike(string text) => text.Replace("%", "\\%").Replace("_", "\\_");
}

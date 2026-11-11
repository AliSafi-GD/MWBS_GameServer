using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Identity;

/// <summary>A login that a provider has verified on the server. Subject is the provider's stable id for the person.</summary>
public sealed record VerifiedIdentity(string Provider, string Subject, string Label);

/// <summary>
/// One way of logging in besides the device id (Google Play Games, phone number, later Apple, CrazyGames...).
/// The client sends a provider-specific "credential" JSON object; the provider proves who it belongs to.
/// Never trust an id the client claims: derive the subject from something the provider verified.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>Short stable name used in URLs and stored in the database, e.g. "googleplay".</summary>
    string Name { get; }

    /// <summary>False when the provider is not configured; such providers are hidden and rejected.</summary>
    bool Enabled { get; }

    /// <summary>Optional first step for providers that need a challenge (the phone provider sends a code).</summary>
    Task BeginAsync(JsonElement request, CancellationToken ct) =>
        throw new GameException("not_supported", $"'{Name}' has no begin step.");

    /// <summary>Verifies the credential and returns the identity. Throws GameException if it is not valid.</summary>
    Task<VerifiedIdentity> VerifyAsync(JsonElement credential, CancellationToken ct);
}

/// <summary>Looks up the enabled providers by name.</summary>
public sealed class IdentityProviders
{
    private readonly Dictionary<string, IIdentityProvider> _byName;

    public IdentityProviders(IEnumerable<IIdentityProvider> providers)
    {
        _byName = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    public IIdentityProvider Get(string name)
    {
        if (!_byName.TryGetValue(name, out var provider) || !provider.Enabled)
            throw new GameException("unknown_provider", $"Login method '{name}' is not available.");
        return provider;
    }

    public IReadOnlyCollection<string> EnabledNames =>
        _byName.Values.Where(p => p.Enabled).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
}

internal static class JsonHelpers
{
    public static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

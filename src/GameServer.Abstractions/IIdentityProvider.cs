using System.Text.Json;

namespace GameServer.Abstractions;

/// <summary>A login that a provider has verified on the server. Subject is the provider's stable id for the person.</summary>
public sealed record VerifiedIdentity(string Provider, string Subject, string Label);

/// <summary>
/// One way of logging in (Google Play Games, phone number, later Apple, a custom one...).
/// The client sends a provider-specific "credential" JSON object; the provider proves who it belongs to.
/// Never trust an id the client claims: derive the subject from something the provider verified.
/// A game module can bring its own with <see cref="IModuleInitializer.RegisterIdentityProvider"/>.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>Short stable name used in URLs and stored in the database, e.g. "googleplay". Lower case.</summary>
    string Name { get; }

    /// <summary>False when the provider is not configured; such providers are hidden and rejected.</summary>
    bool Enabled { get; }

    /// <summary>Optional first step for providers that need a challenge (the phone provider sends a code).</summary>
    Task BeginAsync(JsonElement request, CancellationToken ct) =>
        throw new GameException("not_supported", $"'{Name}' has no begin step.");

    /// <summary>Verifies the credential and returns the identity. Throws GameException if it is not valid.</summary>
    Task<VerifiedIdentity> VerifyAsync(JsonElement credential, CancellationToken ct);
}

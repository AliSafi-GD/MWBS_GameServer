using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Identity;

/// <summary>
/// Looks up the login methods that are switched on, by name. Which ones are on is decided by
/// <see cref="AuthProviderSelection"/> (config GameServer:Auth:Providers or the code builder).
/// Providers come from dependency injection (built-ins, <c>auth: a =&gt; a.Add&lt;T&gt;()</c>) and from game modules.
/// </summary>
public sealed class IdentityProviders
{
    private readonly Lazy<IReadOnlyDictionary<string, IIdentityProvider>> _active;

    public IdentityProviders(IEnumerable<IIdentityProvider> providers, AuthProviderSelection selection, ModuleRegistry modules)
    {
        // Lazy because modules register their providers after the container is built. Program.cs calls Validate()
        // right after loading the modules, so a bad setting stops the server at startup, not at the first login.
        _active = new Lazy<IReadOnlyDictionary<string, IIdentityProvider>>(
            () => selection.Resolve(providers.Concat(modules.IdentityProvidersFromModules)));
    }

    /// <summary>Builds the list now and throws InvalidOperationException if the selection is not valid.</summary>
    public void Validate() => _ = _active.Value;

    public IIdentityProvider Get(string name)
    {
        if (!_active.Value.TryGetValue(name, out var provider))
            throw new GameException("unknown_provider", $"Login method '{name}' is not available.");
        return provider;
    }

    public IReadOnlyCollection<string> EnabledNames =>
        _active.Value.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
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

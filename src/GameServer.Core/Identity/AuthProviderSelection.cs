using System.Text.RegularExpressions;
using GameServer.Abstractions;

namespace GameServer.Core.Identity;

/// <summary>
/// Decides which login methods are on, and checks that the choice makes sense. Pure logic (no database, no HTTP),
/// so it is unit tested. The choice is the setting <c>GameServer:Auth:Providers</c>, e.g. "device,googleplay".
/// <list type="bullet">
/// <item>Not set: the old behavior. Every provider that says <see cref="IIdentityProvider.Enabled"/> is on
/// (device always, googleplay when its credentials are set, phone when Phone:Enabled is true).</item>
/// <item>Set: only the listed providers are on. A name nobody provides, a provider that is listed but not configured,
/// or two providers with one name stop the server at startup (like <see cref="JwtOptions.EnsureValid"/>).</item>
/// </list>
/// </summary>
public sealed partial class AuthProviderSelection
{
    /// <summary>The login method behind <c>POST /v1/auth/device</c>.</summary>
    public const string Device = "device";

    // These would collide with the fixed routes under /v1/auth/.
    private static readonly string[] Reserved = { "refresh", "logout", "providers" };

    private readonly string[]? _listed;

    /// <param name="listed">The chosen names, or null when nothing was chosen (old behavior).</param>
    public AuthProviderSelection(IEnumerable<string>? listed = null)
    {
        _listed = listed?
            .Select(n => n.Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToArray();
        if (_listed is { Length: 0 })
            _listed = null; // an empty list means "not set" (docker compose passes "" when AUTH_PROVIDERS is empty)
    }

    /// <summary>Reads the config value: comma separated, e.g. "device,googleplay". Empty or null means not set.</summary>
    public static AuthProviderSelection Parse(string? value) =>
        new(value?.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>True when the owner chose the providers explicitly.</summary>
    public bool IsSet => _listed is not null;

    public bool IsListed(string name) => _listed is not null && _listed.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the providers that are on, by name. Throws InvalidOperationException with a message that says what to
    /// change if the configuration is wrong.
    /// </summary>
    public IReadOnlyDictionary<string, IIdentityProvider> Resolve(IEnumerable<IIdentityProvider> providers)
    {
        var all = new Dictionary<string, IIdentityProvider>(StringComparer.Ordinal);
        foreach (var p in providers)
        {
            CheckName(p.Name);
            if (!all.TryAdd(p.Name, p))
                throw new InvalidOperationException(
                    $"Two login providers are called '{p.Name}'. A name can be used only once (check your auth: builder and your modules).");
        }

        if (_listed is null)
            return all.Where(kv => kv.Value.Enabled).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var active = new Dictionary<string, IIdentityProvider>(StringComparer.Ordinal);
        foreach (var name in _listed)
        {
            if (!all.TryGetValue(name, out var provider))
                throw new InvalidOperationException(
                    $"GameServer:Auth:Providers lists '{name}' but no login provider has that name. Available: {string.Join(", ", all.Keys.Order())}.");
            if (!provider.Enabled)
                throw new InvalidOperationException(
                    $"Login provider '{name}' is listed in GameServer:Auth:Providers but is not configured." + HintFor(name));
            active[name] = provider;
        }

        return active;
    }

    private static void CheckName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !NameRegex().IsMatch(name))
            throw new InvalidOperationException(
                $"Login provider name '{name}' is not valid: use lower case letters, digits, '-' or '_' (it is part of the URL).");
        if (Reserved.Contains(name))
            throw new InvalidOperationException($"'{name}' cannot be a login provider name: it is a fixed route under /v1/auth/.");
    }

    private static string HintFor(string name) => name switch
    {
        "googleplay" => " Set GameServer:Auth:GooglePlay:ClientId and ClientSecret (env GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET), or remove it from the list.",
        _ => " Configure it, or remove it from the list."
    };

    [GeneratedRegex(@"^[a-z0-9_-]+\z")]
    private static partial Regex NameRegex();
}

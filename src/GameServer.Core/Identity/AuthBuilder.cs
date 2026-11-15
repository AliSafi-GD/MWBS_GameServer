using GameServer.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Core.Identity;

/// <summary>
/// Chooses and extends the login methods in code. Passed to <c>AddGameServerCore(..., auth: a =&gt; ...)</c>.
/// <code>
/// a.Add&lt;AppleIdentityProvider&gt;();          // make your own login available (created through dependency injection)...
/// a.Use("device", "apple");                 // ...and switch on exactly these (overrides GameServer:Auth:Providers)
/// </code>
/// Built-ins that exist without any call: "device", "googleplay", "phone". A game module can add one too, with
/// <c>init.RegisterIdentityProvider(...)</c>.
/// </summary>
public sealed class AuthBuilder
{
    internal List<Action<IServiceCollection>> Registrations { get; } = new();

    internal IReadOnlyList<string>? Pinned { get; private set; }

    /// <summary>Makes a provider available. It still has to be switched on (config list or <see cref="Use"/>).</summary>
    public AuthBuilder Add<T>() where T : class, IIdentityProvider
    {
        Registrations.Add(s => s.AddSingleton<IIdentityProvider>(sp => ActivatorUtilities.CreateInstance<T>(sp)));
        return this;
    }

    /// <summary>Same, with your own factory.</summary>
    public AuthBuilder Add(Func<IServiceProvider, IIdentityProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Registrations.Add(s => s.AddSingleton(factory));
        return this;
    }

    /// <summary>Switches on exactly these login methods and ignores GameServer:Auth:Providers.</summary>
    public AuthBuilder Use(params string[] names)
    {
        if (names.Length == 0 || names.All(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Name at least one login method, e.g. Use(\"device\").", nameof(names));
        Pinned = names;
        return this;
    }
}

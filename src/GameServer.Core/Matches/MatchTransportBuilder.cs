using GameServer.Abstractions;
using GameServer.Core.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Core.Matches;

/// <summary>
/// Chooses and extends the protocols of matches. Passed to <c>AddGameServerCore(..., match: mt => ...)</c>.
/// <code>
/// mt.AddWebSocket();             // from GameServer.Transport.WebSocket: makes "websocket" available
/// mt.AddLiteNetLib();            // from GameServer.Transport.LiteNetLib: makes "udp" available
/// mt.Add&lt;MyQuicTransport&gt;("quic");
/// mt.Use("websocket", "udp");     // optional: pin the enabled ones in code instead of GameServer:Match:Transports
/// </code>
/// </summary>
public sealed class MatchTransportBuilder
{
    internal Dictionary<string, Func<IServiceProvider, IMatchTransport>> Factories { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string>? Pinned { get; private set; }

    public MatchTransportBuilder Add(string name, Func<IServiceProvider, IMatchTransport> factory)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A transport needs a name.", nameof(name));
        Factories[name.Trim().ToLowerInvariant()] = factory;
        return this;
    }

    public MatchTransportBuilder Add<T>(string name) where T : class, IMatchTransport =>
        Add(name, sp => ActivatorUtilities.CreateInstance<T>(sp));

    public MatchTransportBuilder Use(params string[] names)
    {
        Pinned = names.Select(n => n.Trim().ToLowerInvariant()).Distinct().ToArray();
        return this;
    }
}

public static class MatchApplicationExtensions
{
    /// <summary>Lets every enabled match transport add its middleware and endpoints. Call once, after UseAuthorization.</summary>
    public static WebApplication UseGameServerMatches(this WebApplication app)
    {
        foreach (var transport in app.Services.GetServices<IMatchTransport>())
        {
            transport.ConfigureApp(app);
            transport.MapEndpoints(app);
        }

        return app;
    }
}

internal static class MatchRegistration
{
    public static void AddMatchServices(this IServiceCollection services, IConfiguration config, Action<MatchTransportBuilder>? configure)
    {
        var section = config.GetSection("GameServer:Match");
        var options = section.Get<MatchOptions>() ?? new MatchOptions();

        var builder = new MatchTransportBuilder();
        configure?.Invoke(builder);

        var enabled = builder.Pinned ?? options.ParseTransports();
        foreach (var name in enabled)
        {
            if (!builder.Factories.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Match transport '{name}' is enabled in config but no plugin provides it. Registered: {(builder.Factories.Count == 0 ? "(none)" : string.Join(", ", builder.Factories.Keys.Order()))}. Add the transport's project and call its Add...() method in AddGameServerCore(match: ...).");
        }

        services.Configure<MatchOptions>(section);
        services.AddSingleton<MatchModeProvider>();
        services.AddSingleton<MatchService>();
        services.AddSingleton<IMatches>(sp => sp.GetRequiredService<MatchService>());
        services.AddSingleton<IMatchHost>(sp => sp.GetRequiredService<MatchService>());
        services.AddSingleton<Matchmaker>();
        services.AddSingleton<MatchActions>();

        foreach (var name in enabled)
        {
            var factory = builder.Factories[name];
            services.AddSingleton<IMatchTransport>(sp => factory(sp));
        }

        services.AddHostedService<MatchHostedService>();
    }
}

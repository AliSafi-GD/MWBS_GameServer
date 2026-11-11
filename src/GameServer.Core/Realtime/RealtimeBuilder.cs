using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Core.Realtime;

/// <summary>
/// Chooses and extends the protocols of the realtime layer. Passed to <c>AddGameServerCore(..., realtime: rt =&gt; ...)</c>.
/// <code>
/// rt.AddWebSocket().AddTcp();                 // plugins: GameServer.Transport.WebSocket / GameServer.Transport.Tcp
/// rt.Use("websocket", "tcp");                 // pin the protocols in code (overrides the config value)
/// rt.Add&lt;MyQuicTransport&gt;("quic");           // make your own transport available by name...
/// rt.Use("websocket", "quic");                // ...and switch it on
/// </code>
/// </summary>
public sealed class RealtimeBuilder
{
    internal Dictionary<string, Func<IServiceProvider, ITransport>> Factories { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string>? Pinned { get; private set; }

    /// <summary>Makes a transport available under <paramref name="name"/>. It still has to be switched on.</summary>
    public RealtimeBuilder Add(string name, Func<IServiceProvider, ITransport> factory)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A transport needs a name.", nameof(name));
        Factories[name.Trim().ToLowerInvariant()] = factory;
        return this;
    }

    /// <summary>Same, creating the transport through dependency injection.</summary>
    public RealtimeBuilder Add<T>(string name) where T : class, ITransport =>
        Add(name, sp => ActivatorUtilities.CreateInstance<T>(sp));

    /// <summary>Switches on exactly these transports and ignores GameServer:Realtime:Transports.</summary>
    public RealtimeBuilder Use(params string[] names)
    {
        Pinned = names.Select(n => n.Trim().ToLowerInvariant()).Distinct().ToArray();
        return this;
    }
}

public static class RealtimeApplicationExtensions
{
    /// <summary>Lets every enabled transport add its middleware and endpoints. Call once, after UseAuthorization.</summary>
    public static WebApplication UseGameServerRealtime(this WebApplication app)
    {
        foreach (var transport in app.Services.GetServices<ITransport>())
        {
            transport.ConfigureApp(app);
            transport.MapEndpoints(app);
        }

        return app;
    }
}

internal static class RealtimeRegistration
{
    public static void AddRealtimeServices(this IServiceCollection services, IConfiguration config, Action<RealtimeBuilder>? configure)
    {
        var section = config.GetSection("GameServer:Realtime");
        var options = section.Get<RealtimeOptions>() ?? new RealtimeOptions();

        var builder = new RealtimeBuilder();
        configure?.Invoke(builder);

        var enabled = builder.Pinned ?? options.ParseTransports();
        if (enabled.Count == 0)
            throw new InvalidOperationException(
                "No realtime transport is enabled. Set GameServer:Realtime:Transports (e.g. \"websocket\") and register the plugin, e.g. realtime: rt => rt.AddWebSocket().");
        foreach (var name in enabled)
        {
            if (!builder.Factories.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Realtime transport '{name}' is enabled in config but no plugin provides it. Registered: {(builder.Factories.Count == 0 ? "(none)" : string.Join(", ", builder.Factories.Keys.Order()))}. Add the transport's project and call its Add...() method in AddGameServerCore(realtime: ...).");
        }

        services.Configure<RealtimeOptions>(section);
        services.Configure<ChatOptions>(config.GetSection("GameServer:Chat"));

        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<IRealtime>(sp => sp.GetRequiredService<ConnectionManager>());
        services.AddSingleton<RealtimeHub>();
        services.AddSingleton<IConnectionHandler>(sp => sp.GetRequiredService<RealtimeHub>());

        foreach (var name in enabled)
        {
            var factory = builder.Factories[name];
            services.AddSingleton<ITransport>(sp => factory(sp));
        }

        services.AddHostedService<RealtimeHostedService>();
    }
}

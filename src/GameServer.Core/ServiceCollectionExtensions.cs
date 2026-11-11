using GameServer.Abstractions;
using GameServer.Core.Identity;
using GameServer.Core.Matches;
using GameServer.Core.Postgres;
using GameServer.Core.Realtime;
using GameServer.Core.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace GameServer.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core services. Needs ConnectionStrings:Default (env var ConnectionStrings__Default).
    /// <paramref name="realtime"/> chooses the chat/realtime transports in code, e.g. <c>rt =&gt; rt.Use("websocket", "tcp")</c>;
    /// without it the config value GameServer:Realtime:Transports (default "websocket") decides.
    /// </summary>
    public static IServiceCollection AddGameServerCore(
        this IServiceCollection services, IConfiguration config, bool isDevelopment = false,
        Action<RealtimeBuilder>? realtime = null,
        Action<MatchTransportBuilder>? match = null)
    {
        var connectionString = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:Default is required (env var ConnectionStrings__Default).");

        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ModuleLoader>();
        services.AddSingleton<TokenService>();

        // Factory registration so the container disposes the data source on shutdown.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<IStorage, PostgresStorage>();
        services.AddSingleton<IUserStore, PostgresUserStore>();
        services.AddSingleton<IRefreshTokenStore, PostgresRefreshTokenStore>();
        services.AddSingleton<PostgresEconomy>();
        services.AddSingleton<IEconomyStore>(sp => sp.GetRequiredService<PostgresEconomy>());
        services.AddSingleton<IEconomy>(sp => sp.GetRequiredService<PostgresEconomy>());
        services.AddSingleton<PostgresCompetitions>();
        services.AddSingleton<ICompetitions>(sp => sp.GetRequiredService<PostgresCompetitions>());
        services.AddSingleton<ILeaderboards>(sp => sp.GetRequiredService<PostgresCompetitions>());
        services.AddSingleton<GameServer.Core.Shop.ShopService>();
        services.AddSingleton<GameServer.Core.Progression.ProgressService>();
        services.AddSingleton<IProgress>(sp => sp.GetRequiredService<GameServer.Core.Progression.ProgressService>());
        services.AddSingleton<GameServer.Core.Progression.DailyRewardService>();
        services.AddSingleton<PostgresNotifications>();
        services.AddSingleton<INotificationStore>(sp => sp.GetRequiredService<PostgresNotifications>());
        services.AddSingleton<INotifications>(sp => sp.GetRequiredService<PostgresNotifications>());
        services.AddSingleton<PostgresConfigStore>();
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<PostgresConfigStore>());
        services.AddSingleton<IConfig>(sp => sp.GetRequiredService<PostgresConfigStore>());

        // ---- hooks, social, chat ----
        services.AddSingleton<HookRunner>();
        services.AddSingleton<IFriendStore, PostgresFriends>();
        services.AddSingleton<IGroupStore, PostgresGroups>();
        services.AddSingleton<IChatStore, PostgresChat>();
        services.Configure<PartyOptions>(config.GetSection("GameServer:Parties"));
        services.AddSingleton<FriendsService>();
        services.AddSingleton<GroupService>();
        services.AddSingleton<PartyService>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<IChat>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<ISocial, SocialFacade>();
        services.AddSingleton<AccountCleanup>();
        services.AddSingleton<GameServer.Core.Admin.AdminStore>();
        services.AddHostedService<GameServer.Core.Admin.AdminBackground>();
        services.AddRealtimeServices(config, realtime);
        services.AddMatchServices(config, match);

        // ---- login providers (besides the device id) ----
        services.Configure<GooglePlayOptions>(config.GetSection("GameServer:Auth:GooglePlay"));
        services.Configure<PhoneOptions>(config.GetSection("GameServer:Auth:Phone"));

        // SMS: only the logging sender in Development; elsewhere a real gateway must replace NotConfiguredSmsSender.
        // To plug one in, register your ISmsSender before calling this method (TryAdd keeps yours).
        if (isDevelopment)
            services.TryAddSingleton<ISmsSender, LoggingSmsSender>();
        else
            services.TryAddSingleton<ISmsSender, NotConfiguredSmsSender>();

        services.AddHttpClient<GooglePlayIdentityProvider>(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IIdentityProvider>(sp => sp.GetRequiredService<GooglePlayIdentityProvider>());
        services.AddSingleton<IIdentityProvider, PhoneIdentityProvider>();
        services.AddSingleton<IdentityProviders>();

        return services;
    }
}

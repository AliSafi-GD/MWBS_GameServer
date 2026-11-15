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
    /// <paramref name="auth"/> adds or chooses login methods in code, e.g. <c>a =&gt; a.Use("device", "googleplay")</c>;
    /// without a choice there the config value GameServer:Auth:Providers decides (not set = every configured one).
    /// </summary>
    public static IServiceCollection AddGameServerCore(
        this IServiceCollection services, IConfiguration config, bool isDevelopment = false,
        Action<RealtimeBuilder>? realtime = null,
        Action<MatchTransportBuilder>? match = null,
        Action<AuthBuilder>? auth = null)
    {
        var connectionString = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:Default is required (env var ConnectionStrings__Default).");

        // The clock every service reads "now" from. Tests (or a host) can register their own before this call,
        // e.g. a FakeTimeProvider, to move time forward without waiting.
        services.TryAddSingleton(TimeProvider.System);

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
        services.AddSingleton<GameServer.Core.GameConfig.GameConfigStartup>();

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

        // ---- login providers ----
        services.Configure<GooglePlayOptions>(config.GetSection("GameServer:Auth:GooglePlay"));
        services.Configure<PhoneOptions>(config.GetSection("GameServer:Auth:Phone"));

        var authBuilder = new AuthBuilder();
        auth?.Invoke(authBuilder);
        var selection = authBuilder.Pinned is not null
            ? new AuthProviderSelection(authBuilder.Pinned)
            : AuthProviderSelection.Parse(config["GameServer:Auth:Providers"]);
        services.AddSingleton(selection);

        // Once a list exists, being listed is what switches phone on (Phone:Enabled is only the switch for the old,
        // list-less setup). Googleplay is switched on by its credentials, device and custom ones by the list alone.
        if (selection.IsSet)
            services.PostConfigure<PhoneOptions>(o => o.Enabled = selection.IsListed(PhoneIdentityProvider.ProviderName));

        // SMS: only the logging sender in Development; elsewhere a real gateway must replace NotConfiguredSmsSender.
        // To plug one in, register your ISmsSender before calling this method (TryAdd keeps yours).
        if (isDevelopment)
            services.TryAddSingleton<ISmsSender, LoggingSmsSender>();
        else
            services.TryAddSingleton<ISmsSender, NotConfiguredSmsSender>();

        services.AddSingleton<IIdentityProvider, DeviceIdentityProvider>();
        services.AddHttpClient<GooglePlayIdentityProvider>(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IIdentityProvider>(sp => sp.GetRequiredService<GooglePlayIdentityProvider>());
        services.AddSingleton<IPhoneCodeStore, PostgresPhoneCodes>();
        services.AddSingleton<IIdentityProvider, PhoneIdentityProvider>();
        foreach (var register in authBuilder.Registrations)
            register(services);
        services.AddSingleton<IdentityProviders>();

        return services;
    }
}

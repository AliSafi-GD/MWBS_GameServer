using GameServer.Abstractions;
using GameServer.Core.Admin;
using GameServer.Core.Identity;
using GameServer.Core.Progression;
using GameServer.Core.Realtime;
using GameServer.Core.Shop;
using GameServer.Core.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameServer.Infrastructure.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Stores everything in PostgreSQL: registers the data source, the schema migrator (<see cref="DatabaseMigrator"/>,
    /// run it once at startup) and a Postgres implementation of every store interface of GameServer.Core.
    /// Needs ConnectionStrings:Default (env var ConnectionStrings__Default). Use together with AddGameServerCore.
    /// </summary>
    public static IServiceCollection AddGameServerPostgres(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:Default is required (env var ConnectionStrings__Default).");

        // Factory registration so the container disposes the data source on shutdown.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<DatabaseMigrator>();

        // ---- accounts, sessions, player data ----
        services.AddSingleton<IStorage, PostgresStorage>();
        services.AddSingleton<IUserStore, PostgresUserStore>();
        services.AddSingleton<IRefreshTokenStore, PostgresRefreshTokenStore>();
        services.AddSingleton<IPhoneCodeStore, PostgresPhoneCodes>();

        // ---- economy, competitions, progress, shop ----
        services.AddSingleton<PostgresEconomy>();
        services.AddSingleton<IEconomyStore>(sp => sp.GetRequiredService<PostgresEconomy>());
        services.AddSingleton<IEconomy>(sp => sp.GetRequiredService<PostgresEconomy>());
        services.AddSingleton<PostgresCompetitions>();
        services.AddSingleton<ICompetitions>(sp => sp.GetRequiredService<PostgresCompetitions>());
        services.AddSingleton<ILeaderboards>(sp => sp.GetRequiredService<PostgresCompetitions>());
        services.AddSingleton<IShopPurchaseStore, PostgresShopPurchases>();
        services.AddSingleton<IProgressStore, PostgresProgress>();
        services.AddSingleton<IDailyRewardStore, PostgresDailyRewards>();

        // ---- notifications, config ----
        services.AddSingleton<PostgresNotifications>();
        services.AddSingleton<INotificationStore>(sp => sp.GetRequiredService<PostgresNotifications>());
        services.AddSingleton<INotifications>(sp => sp.GetRequiredService<PostgresNotifications>());
        services.AddSingleton<PostgresConfigStore>();
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<PostgresConfigStore>());
        services.AddSingleton<IConfig>(sp => sp.GetRequiredService<PostgresConfigStore>());

        // ---- social, chat ----
        services.AddSingleton<IFriendStore, PostgresFriends>();
        services.AddSingleton<IGroupStore, PostgresGroups>();
        services.AddSingleton<IChatStore, PostgresChat>();

        // ---- admin ----
        services.AddSingleton<IAdminAccountStore, PostgresAdminAccounts>();
        services.AddSingleton<IAdminAuditLog, PostgresAdminAudit>();
        services.AddSingleton<IConfigHistoryStore, PostgresConfigHistory>();
        services.AddSingleton<IPlayerAdminData, PostgresPlayerAdminData>();
        services.AddSingleton<IBroadcastStore, PostgresBroadcasts>();
        services.AddSingleton<IAdminMetrics, PostgresAdminMetrics>();

        return services;
    }
}

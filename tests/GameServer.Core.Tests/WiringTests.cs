using GameServer.Abstractions;
using GameServer.Core.Admin;
using GameServer.Core.Progression;
using GameServer.Core.Shop;
using GameServer.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameServer.Core.Tests;

/// <summary>
/// AddGameServerCore + AddGameServerPostgres, the way the host wires them: every registration can be built, and every
/// store interface of Core has a Postgres implementation. No database connection is opened.
/// </summary>
public class WiringTests
{
    private static IConfiguration Config(bool withConnectionString = true)
    {
        var settings = new Dictionary<string, string?> { ["GameServer:Match:Transports"] = "" };
        if (withConnectionString)
            settings["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x";
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceCollection Services(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment()); // the host adds it
        services.Configure<JwtOptions>(o => o.Secret = "unit-test-secret-unit-test-secret-0123456789");
        services.AddGameServerCore(config, realtime: rt => rt.Add("websocket", _ => throw new NotSupportedException()));
        return services;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void CoreAndPostgres_TogetherResolveEverything()
    {
        var config = Config();
        var services = Services(config);
        services.AddGameServerPostgres(config);

        // ValidateOnBuild checks that every registered service has all its constructor dependencies.
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // One Postgres class behind the three small user interfaces.
        var users = sp.GetRequiredService<PostgresUserStore>();
        Assert.Same(users, sp.GetRequiredService<IAccountStore>());
        Assert.Same(users, sp.GetRequiredService<IIdentityStore>());
        Assert.Same(users, sp.GetRequiredService<IUserAdminStore>());
        Assert.IsType<PostgresEconomy>(sp.GetRequiredService<IEconomy>());
        Assert.IsType<PostgresConfigStore>(sp.GetRequiredService<IConfigStore>());
        Assert.IsType<PostgresShopPurchases>(sp.GetRequiredService<IShopPurchaseStore>());
        Assert.IsType<PostgresDailyRewards>(sp.GetRequiredService<IDailyRewardStore>());
        Assert.IsType<PostgresProgress>(sp.GetRequiredService<IProgressStore>());
        Assert.IsType<PostgresAdminAccounts>(sp.GetRequiredService<IAdminAccountStore>());
        Assert.IsType<PostgresAdminMetrics>(sp.GetRequiredService<IAdminMetrics>());
        Assert.NotNull(sp.GetRequiredService<DatabaseMigrator>());

        // The hook services close a cycle (chat and social run hooks); through Lazy they still resolve.
        var hooks = sp.GetRequiredService<Lazy<HookServices>>().Value;
        Assert.NotNull(hooks.Chat);
        Assert.NotNull(hooks.Social);
        Assert.IsType<GameServer.Core.Realtime.ConnectionManager>(sp.GetRequiredService<GameServer.Core.Realtime.IPlayerPush>());
    }

    [Fact]
    public void MatchRecords_KeepTheirJsonNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new GameServer.Core.Matches.MatchModesView(
                new[] { new GameServer.Core.Matches.MatchModeView("duel", "udp", 2, 2, 20, true, false) },
                new object[] { new { name = "udp", port = 7360 } }),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(
            """{"modes":[{"name":"duel","transport":"udp","minPlayers":2,"maxPlayers":2,"tickRate":20,"matchmaking":true,"allowClientCreate":false}],"transports":[{"name":"udp","port":7360}]}""",
            json);
    }

    [Fact]
    public void CoreAlone_DoesNotNeedAConnectionString()
    {
        var services = Services(Config(withConnectionString: false)); // must not throw
        Assert.DoesNotContain(services, d => d.ServiceType.Namespace == "Npgsql");
    }

    [Fact]
    public void Postgres_WithoutAConnectionString_StopsWithAClearMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddGameServerPostgres(Config(withConnectionString: false)));
        Assert.Contains("ConnectionStrings__Default", error.Message);
    }

    [Fact]
    public void CoreProject_DoesNotReferenceNpgsql()
    {
        var referenced = typeof(ShopService).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("Npgsql", referenced);
    }
}

using GameServer.Abstractions;
using GameServer.Core.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServer.Core.Tests;

/// <summary>
/// Checks the wiring in AddGameServerCore (config value, code builder, the legacy Phone:Enabled switch).
/// No database is touched: the data source is created lazily and never asked for a connection.
/// </summary>
public class AuthRegistrationTests
{
    private static IdentityProviders Build(Dictionary<string, string?> settings, Action<AuthBuilder>? auth = null)
    {
        settings["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x";
        settings["GameServer:Jwt:Secret"] = "unit-test-secret-unit-test-secret-0123456789";
        settings["GameServer:Match:Transports"] = "";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGameServerCore(config,
            realtime: rt => rt.Add("websocket", _ => throw new NotSupportedException()),
            auth: auth);
        return services.BuildServiceProvider().GetRequiredService<IdentityProviders>();
    }

    private sealed class AppleProvider : IIdentityProvider
    {
        public string Name => "apple";
        public bool Enabled => true;
        public Task<VerifiedIdentity> VerifyAsync(System.Text.Json.JsonElement credential, CancellationToken ct) =>
            Task.FromResult(new VerifiedIdentity("apple", "s", "l"));
    }

    [Fact]
    public void Nothing_Set_MeansDeviceOnly()
    {
        Assert.Equal(new[] { "device" }, Build(new()).EnabledNames);
    }

    [Fact]
    public void LegacyPhoneFlag_StillWorksWhenListIsUnset()
    {
        var registry = Build(new() { ["GameServer:Auth:Phone:Enabled"] = "true" });

        Assert.Equal(new[] { "device", "phone" }, registry.EnabledNames);
    }

    [Fact]
    public void ListingPhone_EnablesIt_WithoutThePhoneFlag()
    {
        var registry = Build(new() { ["GameServer:Auth:Providers"] = "device,phone" });

        Assert.Equal(new[] { "device", "phone" }, registry.EnabledNames);
    }

    [Fact]
    public void PhoneFlag_IsIgnoredOnceTheListExists()
    {
        var registry = Build(new() { ["GameServer:Auth:Providers"] = "device", ["GameServer:Auth:Phone:Enabled"] = "true" });

        Assert.Equal(new[] { "device" }, registry.EnabledNames);
    }

    [Fact]
    public void GoogleListedWithoutCredentials_FailsAtValidate()
    {
        var registry = Build(new() { ["GameServer:Auth:Providers"] = "googleplay" });

        Assert.Throws<InvalidOperationException>(registry.Validate);
    }

    [Fact]
    public void GoogleListedWithCredentials_IsOn()
    {
        var registry = Build(new()
        {
            ["GameServer:Auth:Providers"] = "googleplay",
            ["GameServer:Auth:GooglePlay:ClientId"] = "id",
            ["GameServer:Auth:GooglePlay:ClientSecret"] = "secret",
        });

        Assert.Equal(new[] { "googleplay" }, registry.EnabledNames);
    }

    [Fact]
    public void Builder_AddAndUse_OverrideTheConfigValue()
    {
        var registry = Build(
            new() { ["GameServer:Auth:Providers"] = "device,phone" },
            a => a.Add<AppleProvider>().Use("device", "apple"));

        Assert.Equal(new[] { "apple", "device" }, registry.EnabledNames);
    }

    [Fact]
    public void Builder_AddWithoutUse_FollowsTheConfigValue()
    {
        var unlisted = Build(new() { ["GameServer:Auth:Providers"] = "device" }, a => a.Add<AppleProvider>());
        var legacy = Build(new(), a => a.Add<AppleProvider>());

        Assert.Equal(new[] { "device" }, unlisted.EnabledNames);
        Assert.Equal(new[] { "apple", "device" }, legacy.EnabledNames);
    }
}

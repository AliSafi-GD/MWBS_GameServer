using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Identity;
using Xunit;

namespace GameServer.Core.Tests;

/// <summary>A login provider that does nothing; tests only care about its name and Enabled flag.</summary>
internal sealed class FakeProvider : IIdentityProvider
{
    public FakeProvider(string name, bool enabled = true)
    {
        Name = name;
        Enabled = enabled;
    }

    public string Name { get; }
    public bool Enabled { get; }

    public Task<VerifiedIdentity> VerifyAsync(JsonElement credential, CancellationToken ct) =>
        Task.FromResult(new VerifiedIdentity(Name, "subject", "label"));
}

public class AuthProviderSelectionTests
{
    // The built-ins as they look on a fresh server: device on, googleplay and phone not configured.
    private static IIdentityProvider[] BuiltIns(bool googleConfigured = false, bool phoneEnabled = false) => new IIdentityProvider[]
    {
        new DeviceIdentityProvider(),
        new FakeProvider("googleplay", googleConfigured),
        new FakeProvider("phone", phoneEnabled),
    };

    private static string[] Names(IReadOnlyDictionary<string, IIdentityProvider> active) => active.Keys.Order().ToArray();

    // ---- setting not set: the old behavior ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void NotSet_IsLegacy_DeviceOnlyWhenNothingElseIsConfigured(string? setting)
    {
        var selection = AuthProviderSelection.Parse(setting);

        Assert.False(selection.IsSet);
        Assert.Equal(new[] { "device" }, Names(selection.Resolve(BuiltIns())));
    }

    [Fact]
    public void NotSet_EnablesEveryConfiguredProvider()
    {
        var active = AuthProviderSelection.Parse(null).Resolve(BuiltIns(googleConfigured: true, phoneEnabled: true));

        Assert.Equal(new[] { "device", "googleplay", "phone" }, Names(active));
    }

    [Fact]
    public void NotSet_ModuleProviderIsOnWhenEnabled_AndOffWhenNot()
    {
        var all = BuiltIns().Concat(new IIdentityProvider[] { new FakeProvider("apple"), new FakeProvider("email", enabled: false) });

        var active = AuthProviderSelection.Parse(null).Resolve(all);

        Assert.Equal(new[] { "apple", "device" }, Names(active));
    }

    // ---- setting set ----

    [Fact]
    public void Set_DeviceOnly_IgnoresConfiguredProviders()
    {
        var active = AuthProviderSelection.Parse("device").Resolve(BuiltIns(googleConfigured: true, phoneEnabled: true));

        Assert.Equal(new[] { "device" }, Names(active));
    }

    [Fact]
    public void Set_GoogleOnly_TurnsDeviceOff()
    {
        var active = AuthProviderSelection.Parse("googleplay").Resolve(BuiltIns(googleConfigured: true));

        Assert.Equal(new[] { "googleplay" }, Names(active));
    }

    [Fact]
    public void Set_IsCaseAndSpaceTolerant()
    {
        var active = AuthProviderSelection.Parse(" Device , GooglePlay ").Resolve(BuiltIns(googleConfigured: true));

        Assert.Equal(new[] { "device", "googleplay" }, Names(active));
    }

    [Fact]
    public void Set_ListedButNotConfigured_Throws_WithAHint()
    {
        var selection = AuthProviderSelection.Parse("device,googleplay");

        var ex = Assert.Throws<InvalidOperationException>(() => selection.Resolve(BuiltIns(googleConfigured: false)));

        Assert.Contains("googleplay", ex.Message);
        Assert.Contains("ClientId", ex.Message);
    }

    [Fact]
    public void Set_UnknownName_Throws_AndListsWhatExists()
    {
        var selection = AuthProviderSelection.Parse("device,facebok");

        var ex = Assert.Throws<InvalidOperationException>(() => selection.Resolve(BuiltIns()));

        Assert.Contains("facebok", ex.Message);
        Assert.Contains("googleplay", ex.Message);
    }

    [Fact]
    public void DuplicateProviderNames_Throw_WhetherOrNotTheSettingIsSet()
    {
        var all = BuiltIns().Concat(new IIdentityProvider[] { new FakeProvider("phone") });

        Assert.Throws<InvalidOperationException>(() => AuthProviderSelection.Parse(null).Resolve(all));
        Assert.Throws<InvalidOperationException>(() => AuthProviderSelection.Parse("device").Resolve(all));
    }

    [Fact]
    public void ModuleProvider_MustBeListedWhenTheSettingIsSet()
    {
        var all = BuiltIns().Concat(new IIdentityProvider[] { new FakeProvider("apple") });

        Assert.Equal(new[] { "apple", "device" }, Names(AuthProviderSelection.Parse("device,apple").Resolve(all)));
        Assert.Equal(new[] { "device" }, Names(AuthProviderSelection.Parse("device").Resolve(all)));
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("logout")]
    [InlineData("providers")]
    [InlineData("Apple")]
    [InlineData("a b")]
    [InlineData("")]
    public void BadProviderNames_Throw(string name)
    {
        var all = BuiltIns().Concat(new IIdentityProvider[] { new FakeProvider(name) });

        Assert.Throws<InvalidOperationException>(() => AuthProviderSelection.Parse(null).Resolve(all));
    }

    // ---- the registry the endpoints use ----

    [Fact]
    public void Registry_DeviceDisabled_GetThrowsUnknownProvider()
    {
        var registry = new IdentityProviders(BuiltIns(googleConfigured: true), AuthProviderSelection.Parse("googleplay"), new ModuleRegistry());

        var ex = Assert.Throws<GameException>(() => registry.Get("device"));

        Assert.Equal("unknown_provider", ex.Code);
        Assert.Equal(new[] { "googleplay" }, registry.EnabledNames);
    }

    [Fact]
    public void Registry_DeviceEnabled_IsListedAndGetWorks()
    {
        var registry = new IdentityProviders(BuiltIns(), AuthProviderSelection.Parse("device"), new ModuleRegistry());

        Assert.Equal("device", registry.Get("device").Name);
        Assert.Equal(new[] { "device" }, registry.EnabledNames);
    }

    [Fact]
    public async Task DeviceProvider_CannotVerifyAnything_SoLinkingItIsRejected()
    {
        var ex = await Assert.ThrowsAsync<GameException>(
            () => new DeviceIdentityProvider().VerifyAsync(default, CancellationToken.None));

        Assert.Equal("not_supported", ex.Code);
    }

    [Fact]
    public void Registry_UnknownName_ThrowsUnknownProvider()
    {
        var registry = new IdentityProviders(BuiltIns(), AuthProviderSelection.Parse(null), new ModuleRegistry());

        Assert.Equal("unknown_provider", Assert.Throws<GameException>(() => registry.Get("nope")).Code);
        Assert.Equal("unknown_provider", Assert.Throws<GameException>(() => registry.Get("googleplay")).Code); // not configured
    }

    [Fact]
    public void Registry_ProvidersFromModules_FollowTheSameRule()
    {
        var modules = new ModuleRegistry();
        modules.RegisterIdentityProvider(new FakeProvider("apple"));

        var listed = new IdentityProviders(BuiltIns(), AuthProviderSelection.Parse("device,apple"), modules);
        var unlisted = new IdentityProviders(BuiltIns(), AuthProviderSelection.Parse("device"), modules);
        var legacy = new IdentityProviders(BuiltIns(), AuthProviderSelection.Parse(null), modules);

        Assert.Equal("apple", listed.Get("apple").Name);
        Assert.Equal("unknown_provider", Assert.Throws<GameException>(() => unlisted.Get("apple")).Code);
        Assert.Equal("apple", legacy.Get("apple").Name);
    }

    [Fact]
    public void Registry_Validate_FailsFast_OnDuplicateFromModule()
    {
        var modules = new ModuleRegistry();
        modules.RegisterIdentityProvider(new FakeProvider("phone"));
        var registry = new IdentityProviders(BuiltIns(), AuthProviderSelection.Parse(null), modules);

        Assert.Throws<InvalidOperationException>(registry.Validate);
    }

    [Fact]
    public void Registry_Validate_FailsFast_OnMisconfiguredList()
    {
        var registry = new IdentityProviders(BuiltIns(), AuthProviderSelection.Parse("googleplay"), new ModuleRegistry());

        Assert.Throws<InvalidOperationException>(registry.Validate);
    }
}

using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core.Identity;

/// <summary>
/// Guest login, listed like every other login method so it can be switched on or off with GameServer:Auth:Providers.
/// It does not verify anything itself: device ids are not stored as identities and the login lives at its own
/// endpoint, <c>POST /v1/auth/device</c>, which asks <see cref="IdentityProviders.Get"/> whether "device" is on.
/// </summary>
public sealed class DeviceIdentityProvider : IIdentityProvider
{
    public string Name => AuthProviderSelection.Device;

    // Always "configured": whether it is on is the selection's decision.
    public bool Enabled => true;

    // Reached only through the generic routes (e.g. /v1/account/link/device), which cannot work for a device id.
    public Task<VerifiedIdentity> VerifyAsync(JsonElement credential, CancellationToken ct) =>
        throw new GameException("not_supported", "Device login has its own endpoint (POST /v1/auth/device) and cannot be used here.");
}

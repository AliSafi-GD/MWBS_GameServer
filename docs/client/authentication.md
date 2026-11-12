# Authentication

## Guest login

```csharp
if (!await client.Auth.TryRestoreSessionAsync())
    await client.Auth.LoginDeviceAsync();
```

`TryRestoreSessionAsync` returns true if a saved session is still valid. `LoginDeviceAsync(username = null, create = true, deviceId = null)`
returns a `LoginResult`. The device id is generated and saved by the SDK.

With `Storage = new PlayerPrefsStorage()` the session is kept between runs.

## Other providers

```csharp
var providers = await client.Auth.ProvidersAsync();                   // e.g. ["googleplay"]
await client.Auth.LoginAsync("googleplay", new { serverAuthCode = code }, create: false);
```

For providers that send a code (phone), call `BeginAsync` first, then `LoginAsync` with the code:

```csharp
await client.Auth.BeginAsync("phone", new { phone = "+989121234567" });
await client.Auth.LoginAsync("phone", new { phone = "+989121234567", code = "123456" });
```

## Save progress: link a login

A guest can attach another login so progress survives a lost phone:

```csharp
await client.Account.LinkAsync("googleplay", new { serverAuthCode = code });
await client.Account.UnlinkAsync("googleplay");
```

Errors: `identity_in_use` (that login belongs to another account), `provider_already_linked`, `last_login_method`.

## Logout and session end

```csharp
await client.Auth.LogoutAsync();            // this device
await client.Auth.LogoutEverywhereAsync();  // every device
client.Auth.SessionEnded += () => ShowLogin();
```

Access tokens are refreshed automatically. `SessionEnded` fires only when the refresh fails and the player must sign in again
(see [Authentication on the server](../server/authentication#sessions-and-refresh-tokens)).

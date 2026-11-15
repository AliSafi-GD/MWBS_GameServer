# Authentication

All login methods return the same JWT. Guests start with a device id. Linking another method makes the account recoverable
on a new phone.

## Login methods

| Endpoint | What it does |
|---|---|
| `POST /v1/auth/device` | Guest login `{deviceId, username?, create?}` |
| `GET /v1/auth/providers` | Login methods that are on, e.g. `["device","googleplay"]` (`device` is the guest login) |
| `POST /v1/auth/{provider}/begin` | First step (phone: sends the code) `{credential:{phone}}` |
| `POST /v1/auth/{provider}` | Login `{credential, create?, username?}`. `create:false` means restore only (404 if unknown) |
| `POST /v1/account/link/{provider}` | Attach a login to the signed-in account (409 `identity_in_use`, `provider_already_linked`) |
| `DELETE /v1/account/link/{provider}` | Detach (403 `last_login_method` if it is the only one left) |

A typical flow: the game starts as a guest (device) -> the player taps "Save my progress" -> the client gets a Play Games server
auth code and calls `link/googleplay`. On a new phone: `POST /v1/auth/googleplay` with `create:false`.

### Guest (device)

On unless you leave `device` out of `GameServer:Auth:Providers`. It cannot be linked (`link/device` answers `400 not_supported`):
it is where an account starts, and device ids are not stored as logins.

```bash
curl -X POST http://localhost:5000/v1/auth/device -H "Content-Type: application/json" \
  -d '{"deviceId":"my-test-device-0001"}'
```

Device ids act like passwords, so the admin API only shows them masked.

### Google Play Games

Credential: `{ "serverAuthCode": "..." }`. Also used for Bazaar and Myket builds.

1. Create a **Web application** OAuth client in Google Cloud.
2. Link it in Play Console (Play Games Services).
3. Set `GameServer__Auth__GooglePlay__ClientId` and `ClientSecret` (`GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET` in `.env`).

The server must be able to reach `oauth2.googleapis.com` and `www.googleapis.com`. Players without Google Play Services
can still use the guest login.

### Phone (OTP)

Credential: `{ "phone": "+989121234567", "code": "123456" }`. Enable with `GameServer__Auth__Phone__Enabled=true`.
Codes are hashed, expire after 5 minutes, allow one code per 60 seconds per number and 5 attempts.

::: warning SMS delivery is not built in
In Development the code is printed in the server log. Elsewhere `begin` answers `503 sms_not_configured` until you
register your own `ISmsSender` before `AddGameServerCore`.
:::

## Choosing the login methods

Every game picks its own set of login methods. Built in: `device` (guest), `googleplay` and `phone`. The setting
`GameServer:Auth:Providers` is a comma separated list, like the transports (env `GameServer__Auth__Providers`,
`AUTH_PROVIDERS` in `.env`):

| Value | Result |
|---|---|
| `device` | Guest only. Google Play and phone are off even if configured |
| `device,googleplay` | Guest plus Google Play Games |
| `googleplay` | Google only. No guest login: `POST /v1/auth/device` answers `404 unknown_provider` |
| *(empty, the default)* | Guest, plus Google Play when its credentials are set, plus phone when `Phone:Enabled` is true |

Rules once the list is set:

- Only the listed methods are on. A method that is not listed is rejected with `404 unknown_provider`, as if it did not exist.
- The server **refuses to start** if a listed name does not exist (typo), if a listed method is not configured (`googleplay`
  without `ClientId` and `ClientSecret`) or if two providers share a name. The message says what to change.
- Listing `phone` switches it on. `Phone:Enabled` is only the old switch for servers without a list and is ignored once
  there is one.
- `GET /v1/auth/providers` returns exactly the methods that are on, so a client can show only those buttons.

::: tip Upgrading
Nothing changes for an existing server: with no list set, guest login stays on, Google Play stays on when its credentials
are set and phone stays on when `Phone:Enabled` is true. Set the list only when you want to narrow it.
:::

The same choice can be made in code, which wins over the setting (as with realtime transports):

```csharp
builder.Services.AddGameServerCore(builder.Configuration, builder.Environment.IsDevelopment(),
    auth: a => a.Use("device", "googleplay"));
```

## Writing your own provider

A provider proves who a player is. Implement `IIdentityProvider` (from `GameServer.Abstractions`):

```csharp
public sealed class AppleIdentityProvider : IIdentityProvider
{
    public string Name => "apple";            // lower case; it is part of the URL: POST /v1/auth/apple
    public bool Enabled => true;              // false = "not configured", e.g. a missing key

    public async Task<VerifiedIdentity> VerifyAsync(JsonElement credential, CancellationToken ct)
    {
        var token = credential.GetProperty("identityToken").GetString();
        var appleUserId = await CheckWithApple(token, ct);     // your code: never trust an id the client sends
        if (appleUserId is null) throw new GameException("invalid_credential", "Apple rejected the token.");
        return new VerifiedIdentity("apple", appleUserId, "Apple user");   // Provider, Subject, Label
    }
}
```

`VerifyAsync` is all that is required. Add `BeginAsync` when the login has a first step (the phone provider sends a code
there). Throw `GameException` for failures: `invalid_credential` gives `401`, `provider_unavailable` gives `502`.
The server then creates or finds the account and issues the tokens, and `link/apple` works too.

There are two ways to add it:

**1. In the host project, with the builder.** The class is created through dependency injection, so it can take
`HttpClient`, `IOptions<...>` and so on in its constructor.

```csharp
builder.Services.AddGameServerCore(builder.Configuration, builder.Environment.IsDevelopment(),
    auth: a => a.Add<AppleIdentityProvider>().Use("device", "apple"));
```

Without `.Use(...)` the provider is available but follows the setting (`AUTH_PROVIDERS=device,apple`). A provider registered
directly with `services.AddSingleton<IIdentityProvider, AppleIdentityProvider>()` (before or after `AddGameServerCore`) works the same.

**2. In a game module**, so the game brings its login with its DLL and the server needs no code change:

```csharp
public sealed class MyModule : IServerModule
{
    public void Initialize(IModuleInitializer init)
    {
        init.RegisterIdentityProvider(new AppleIdentityProvider());
    }
}
```

A module provider follows the same rule: when `GameServer:Auth:Providers` is set it must be listed
(`device,apple`); when it is empty the provider is on if its `Enabled` is true. Module providers are created by you with
`new`, so read your keys from environment variables or your own files. The check happens once, right after the modules load.

Names cannot be used twice, and `refresh`, `logout` and `providers` are taken by the server's own routes.

## Sessions and refresh tokens

Every login returns `token` (JWT, 60 minutes) and `refreshToken` (30 days, **single use**).

1. When `token` expires (HTTP 401) call `POST /v1/auth/refresh {refreshToken}`. You get a new pair. Store the new `refreshToken`.
2. Reusing an old or unknown refresh token returns `401 invalid_refresh_token` and **ends that session**. A stolen token is
   detected the next time either side uses it. Log in again (guests: `/v1/auth/device`, silently).
3. `POST /v1/auth/logout {refreshToken}` ends one device's session. `POST /v1/account/logout-all` ends all of them.

If a refresh response is lost on a bad connection, the client has already burned its token and must log in again.
The Unity SDK does all of this for you and raises `SessionEnded` only when the player really must sign in again.

## Banned and deleted accounts

They are rejected on every request, even with a still-valid token, and cannot refresh.

## Rate limits

Counted per client IP:

| Endpoints | Limit |
|---|---|
| `begin`, provider login, `refresh`, `logout`, account `link` | 20 per minute |
| `POST /v1/auth/device` (guest login) | 30 per minute |
| `GET /v1/config`, `GET /v1/version-check` | 120 per minute |
| Admin API | 600 per minute |

Guest login used to have no limit, so anyone could create accounts without end. It now has its own limit, higher than the
others because many players can share one IP (mobile networks, a school). Behind a reverse proxy enable forwarded headers so the
real client IP is counted. A limited call answers HTTP 429.

# Authentication

All login methods return the same JWT. Guests start with a device id. Linking another method makes the account recoverable
on a new phone.

## Login methods

| Endpoint | What it does |
|---|---|
| `POST /v1/auth/device` | Guest login `{deviceId, username?, create?}` |
| `GET /v1/auth/providers` | Enabled providers, e.g. `["googleplay"]` |
| `POST /v1/auth/{provider}/begin` | First step (phone: sends the code) `{credential:{phone}}` |
| `POST /v1/auth/{provider}` | Login `{credential, create?, username?}`. `create:false` means restore only (404 if unknown) |
| `POST /v1/account/link/{provider}` | Attach a login to the signed-in account (409 `identity_in_use`, `provider_already_linked`) |
| `DELETE /v1/account/link/{provider}` | Detach (403 `last_login_method` if it is the only one left) |

A typical flow: the game starts as a guest (device) -> the player taps "Save my progress" -> the client gets a Play Games server
auth code and calls `link/googleplay`. On a new phone: `POST /v1/auth/googleplay` with `create:false`.

### Guest (device)

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

Login endpoints are limited per IP (20 per minute; admin API 600 per minute). Behind a reverse proxy enable forwarded
headers so the real client IP is counted. A limited call answers HTTP 429.

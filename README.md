# GameServer

A small, Nakama-style game backend in C#. One server instance per game; game logic lives in a module DLL.

```
src/GameServer.Abstractions   interfaces modules compile against (version carefully, only add)
src/GameServer.Core           module loader, auth (JWT), accounts/profiles, Postgres storage + migrations
src/GameServer.Host           ASP.NET Core executable, admin panel (wwwroot/admin), Dockerfile
samples/HelloGame.Modules     example game module
```

## Run locally (Git Bash or any shell, from this folder)

```bash
# 1. Start Postgres only (needs Docker). The password comes from .env.
cp .env.example .env
docker compose up -d postgres

# 2. Build the sample module into modules/<Name>/
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules

# 3. Run the host (launchSettings.json sets Development + http://localhost:5000;
#    appsettings.Development.json has the dev DB connection, JWT secret and admin key).
#    Tables are created/upgraded automatically at startup.
dotnet run --project src/GameServer.Host
```

## Admin panel

Open <http://localhost:5000/admin> and sign in with the admin key
(in Development: `dev-admin-key-do-not-use-in-production-0123456789`).

You can search players, edit their profile, ban/unban, export their data and delete accounts.
The panel talks to `/admin/api/*`, which requires the `X-Admin-Key` header.

### Admin console: dashboard, roles, audit, live-ops

Tabs added on top of the user/config tools:

- **Dashboard**: online players, live matches, matchmaking queue, new/active players, 24 h economy (earned/spent per currency),
  charts for online players and matches (a sample is saved every minute and kept for 7 days). Refreshes every 10 s.
  API: `GET /admin/api/dashboard`, `GET /admin/api/dashboard/series?minutes=`.
- **Admins** and **Audit log** (Admin role only). The key from settings is always `root` with the Admin role. Create more admins
  with `POST /admin/api/admins {name, role}`; the key (`gsk_...`) is shown once and only its hash is stored (`POST /admins/{id}/reset-key`
  replaces it). Roles: **Admin** (everything), **Support** (read everything; ban/unban, mute, disconnect, send messages to a player,
  delete chat messages, kick/ban group members), **ReadOnly** (read, except admins and audit). Denied calls answer `403 insufficient_role`.
  Every write is saved in the audit log (who, role, method, path, start of the body with secrets hidden, status, IP; kept 180 days):
  `GET /admin/api/audit?actor=&q=&before=&limit=`. Denied attempts are logged too.
- **Users > Storage / Timeline**: edit or delete a player's storage keys (`GET/PUT/DELETE /admin/api/users/{id}/storage[/{key}]`) and see one
  merged timeline of wallet changes, inbox, chat, account events and admin actions (`GET /admin/api/users/{id}/timeline`).
- **Live-Ops**: scheduled inbox messages with an optional reward, sent to all players or only those online at send time
  (`POST /admin/api/liveops/broadcasts {runAtUtc?, subject, body, reward, audience: "all"|"online", expiresInDays}`, checked every ~15 s;
  `GET` lists, `DELETE` cancels), and a tournament editor (writes the config key `tournaments`; extend by 7 days, standings, delete).
- **Config > History**: every config change keeps the previous value (last 50 per key). `GET /admin/api/config/{key}/history`,
  `POST /admin/api/config/{key}/rollback/{historyId}`.
- **Modules**: the RPCs, match handlers and transports that the loaded modules registered, and an **RPC tester** that runs an RPC as a chosen
  player, hooks included (`GET /admin/api/modules`, `POST /admin/api/rpc-test {userId, id, payload}`). It changes real data.

Database: migration 12 adds `admin_accounts`, `admin_audit`, `config_history`, `scheduled_broadcasts` and `metrics_samples`.

Set `GameServer__Admin__Key` (min 24 chars) to enable it elsewhere; leave it empty to disable the panel and API.
In production serve it over HTTPS and, ideally, restrict `/admin` to your own IP or VPN.

## Player API

| Method | Path | Auth | What |
|---|---|---|---|
| POST | `/v1/auth/device` | none | `{deviceId, username?, create?}` -> JWT. Creates the account on first login |
| GET | `/v1/account` | Bearer | My profile |
| PUT | `/v1/account` | Bearer | Change `username`, `displayName`, `avatar`, `lang`, `timezone` (send only what changes) |
| DELETE | `/v1/account` | Bearer | Delete my account and everything stored for it |
| GET | `/v1/users?ids=a,b,c` | Bearer | Public profiles of other players (max 100) |
| POST | `/v1/rpc/{id}` | Bearer | Call a module RPC |
| GET | `/healthz` | none | Status and loaded RPCs |

Rules: `username` is 3-20 chars of English letters, digits and `_` (stored lower-case, unique, some names reserved).
`displayName` is free text up to 32 characters in any language (control and bidi-override characters are rejected).
`avatar` is an id like `avatar_07` or an https URL. Errors look like `{ "error": "username_taken", "message": "..." }`.

Banned or deleted accounts are rejected on every request, even with a still-valid token.

## Try it

```bash
curl -X POST http://localhost:5000/v1/auth/device \
  -H "Content-Type: application/json" -d '{"deviceId":"my-test-device-0001"}'

TOKEN="<paste token here>"
curl http://localhost:5000/v1/account -H "Authorization: Bearer $TOKEN"
curl -X PUT http://localhost:5000/v1/account -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"username":"ali_99","displayName":"علی"}'
curl -X POST http://localhost:5000/v1/rpc/claim_daily_reward -H "Authorization: Bearer $TOKEN"
```

## Run everything in Docker

```bash
cp .env.example .env        # then edit JWT_SECRET, ADMIN_KEY and DB_PASSWORD
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules
docker compose up --build   # server on http://localhost:7350, panel on /admin
```

Outside Development the server refuses to start without `GameServer__Jwt__Secret` (min 32 bytes)
and `ConnectionStrings__Default`.

## Database

Tables are created by `DatabaseMigrator` at startup and tracked in `schema_migrations`.
To change the schema, add a new numbered migration; never edit an applied one.

- `users`: id, username, display_name, avatar, lang, timezone, created/updated/last-login times, disabled
- `user_devices`: device id -> user (guest login; a user can have several devices)
- `user_identities`: (provider, subject) -> user; at most one per provider per user (Google Play, phone, ...)
- `phone_otps`: hashed one-time codes for phone login
- `refresh_tokens`: hashed refresh tokens grouped in sessions (family)
- `config`: remote config (free-form JSON per key, public or server-only)
- `wallet_balances`, `inventory_items`, `ledger`: currencies, stackable items and their permanent history
- `leaderboard_scores`, `tournament_entries`: scores per board and period, tournament participation
- `notifications`: player inbox (messages with optional claimable reward)
- `storage`: per-user JSON documents used by game modules
Deleting a user cascades to devices and storage. Device ids act like passwords, so the admin API only shows them masked.

## Login methods

All methods give the same JWT. Guests start with the device id; linking another method makes the account
recoverable on a new phone.

| Endpoint | What it does |
|---|---|
| `POST /v1/auth/device` | guest login `{deviceId, username?, create?}` |
| `GET /v1/auth/providers` | enabled providers, e.g. `["googleplay"]` |
| `POST /v1/auth/{provider}/begin` | first step (phone: sends the code) `{credential:{phone}}` |
| `POST /v1/auth/{provider}` | login `{credential, create?, username?}`; `create:false` = restore only (404 if unknown) |
| `POST /v1/account/link/{provider}` | attach a login to the signed-in account (409 `identity_in_use`, `provider_already_linked`) |
| `DELETE /v1/account/link/{provider}` | detach (403 `last_login_method` if it is the only login left) |

Typical flow: game starts as guest (device) -> player taps "Save my progress" -> client gets a Play Games
server auth code and calls `link/googleplay`. On a new phone: `POST /v1/auth/googleplay` with `create:false`.

**googleplay** (also used for the Bazaar and Myket builds): credential `{ "serverAuthCode": "..." }`.
Create a "Web application" OAuth client in Google Cloud, link it in Play Console (Play Games Services),
then set `GameServer__Auth__GooglePlay__ClientId/ClientSecret` (`GOOGLE_CLIENT_ID/SECRET` in `.env`).
The server must be able to reach `oauth2.googleapis.com` and `www.googleapis.com`. Players without Google Play
Services can still use the guest login.

**phone**: credential `{ "phone": "+989121234567", "code": "123456" }`. Enable with
`GameServer__Auth__Phone__Enabled=true`. Codes are hashed, expire after 5 minutes, one code per 60 s per number,
5 attempts. SMS delivery is **not implemented**: in Development the code is printed in the server log; elsewhere
`begin` answers 503 `sms_not_configured` until you register your own `ISmsSender` before `AddGameServerCore`.

Login endpoints are rate limited per IP (20/min; admin API 600/min). Behind a reverse proxy, enable forwarded
headers so the real client IP is counted.

## Sessions (refresh tokens)

Every login returns `token` (JWT, 60 min) and `refreshToken` (30 days, single use). When `token` expires
(401), call `POST /v1/auth/refresh {refreshToken}` to get a new pair and store the new `refreshToken`.
Reusing an old refresh token (or an unknown one) returns 401 `invalid_refresh_token` and ends that session, so a
stolen token is detected the next time either side uses it. Then log in again (guests: `/v1/auth/device`, silent).
`POST /v1/auth/logout {refreshToken}` ends one device's session; `POST /v1/account/logout-all` ends all.
Tune with `GameServer__Jwt__ExpiryMinutes` and `GameServer__Jwt__RefreshDays`. Banned accounts cannot refresh.
If a refresh response is lost on a bad connection, the client has already burned its token and must log in again.

## Remote config, version check, maintenance

Edit in the admin panel (Config tab) or `PUT /admin/api/config/{key}` with `{ "value": <any JSON>, "public": bool }`.
- `GET /v1/config` (no login): all **public** keys as `{ "config": { key: value } }`, with ETag/`If-None-Match` (304).
  Non-public keys never leave the server; modules read any key with `ctx.Config.GetAsync<T>("key")`
  (camelCase, case-insensitive, cached ~5 s).
- `app_version`: `{ "default": {"latest","minimum","url"}, "bazaar": {...}, "googleplay": {...} }`.
  `GET /v1/version-check?store=bazaar&version=1.2.0` -> `{ status: ok | update_available | update_required, latest, minimum, url }`.
  Unknown store falls back to `default`; without any rule the answer is `ok`.
- `maintenance`: `{ "enabled": true, "message": "...", "until": "..." }` (always public). While enabled every `/v1/*` call
  except `/v1/config` and `/v1/version-check` answers `503 {error:"maintenance", message, until}`. The admin API and
  panel keep working. The panel has a one-click switch.

Typical client start: `version-check` -> `config` -> login. Changes apply within ~5 s (per server instance).

## Wallet and inventory

Games define their own currency and item names (lowercase, e.g. `coins`, `card_fire`); the server enforces the rules:
balances never go below 0, a transaction is all-or-nothing, and every change is saved in the `ledger` (reason, amount,
balance after). Only modules change balances; clients can only read them (`GET /v1/wallet`, `GET /v1/inventory`).

```csharp
// in a module RPC: pay 100 coins and receive a card, together or not at all
var tx = new EconomyTransaction("buy_card").Currency("coins", -100).Item("card_fire", 1);
var result = await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken); // throws insufficient_funds / insufficient_items
```
- Pass an idempotency key as the 2nd constructor argument for operations that must happen once
  (`new EconomyTransaction("daily_reward", "daily:20261108")`); a repeat returns `result.AlreadyApplied == true`.
- Errors: `insufficient_funds` / `insufficient_items` (409), `invalid_transaction` (400).
- **Catalog:** save config key `economy` (`{ "currencies": { "coins": { "name": ..., "max": 999 } }, "items": { "card_fire": { "stack": 99 } } }`) and the wallet only accepts those names (`unknown_currency` / `unknown_item`) and enforces `max` / `stack` (`limit_reached`). Names/icons are free JSON for your client, public keys reach it via `GET /v1/config`. Without the key nothing is enforced.
- Inventory counts stackable items per id. Per-item data (level, upgrades) belongs in the module's storage for now.
- Admin panel (user detail): balances, adjust form (reason required) and history. API: `GET/POST /admin/api/users/{id}/economy`,
  `GET /admin/api/users/{id}/ledger`. The privacy export includes balances.

## Leaderboards and tournaments

Defined in config (Config tab has templates), read by clients, written only by modules.
```json
"leaderboards": { "weekly_score": { "sort": "desc", "operator": "best", "reset": "weekly" } }
"tournaments":  { "winter_cup": { "name": {"en":"Winter Cup"}, "start": "2026-12-01T00:00:00Z", "end": "2026-12-08T00:00:00Z",
                  "entryFee": { "coins": 50 }, "rewards": [ { "fromRank": 1, "toRank": 1, "wallet": { "coins": 1000 } },
                                                            { "fromRank": 2, "toRank": 10, "wallet": { "coins": 200 } } ] } }
```
- `operator`: `best` keeps the better score, `set` overwrites, `incr` adds. `reset`: `never|daily|weekly|monthly` (UTC).
  There is no reset job: a new day/week/month is just a new period key (`2026-W46`), and old periods stay readable
  with `?period=`. Ties go to whoever reached the score first. Banned players are hidden.
- Module: `ctx.Leaderboards.SubmitAsync("weekly_score", ctx.UserId, score)` and `SubmitTournamentAsync("winter_cup", ...)`
  (needs join + running). Validate the score in your module first; the sample RPC `submit_score` shows it.
- Client: `GET /v1/leaderboards/{id}?limit=50&around=0&period=current` (top + `me`; `around=N` = N above/below me),
  `GET /v1/tournaments`, `GET /v1/tournaments/{id}` (info + standings), `POST /v1/tournaments/{id}/join` (pays the fee once),
  `POST /v1/tournaments/{id}/claim` (after the end: reward for the final rank, once; needs a score).
- Admin: Leaderboards tab (view, remove a cheater), `GET /admin/api/leaderboards/{id}` (`tournament:<id>` for tournaments).
- Tournament rewards are claimed by the player (no background job), so unclaimed rewards simply stay claimable.

## Notifications

A per-player inbox: messages with an optional reward the player claims (paid once through the wallet, with an idempotency key).
- Module: `ctx.Notifications.SendAsync(userId, "Subject", "Body", new NotificationReward { Wallet = new() { ["coins"] = 50 } }, expiresUtc)`.
- Client: `GET /v1/notifications?unread=true`, `POST /v1/notifications/{id}/read`, `POST /v1/notifications/{id}/claim`, `DELETE /v1/notifications/{id}`
  (an unclaimed reward must be claimed first). Expired ones are hidden and cannot be claimed.
- Live: when a notification is saved, online players get the realtime event `notification.new` `{ id, broadcast, subject, hasReward }` (`id` is null for broadcasts); the client then reads the inbox. Offline players read it at next login.
- Admin: panel (user detail) sends to one player or to everyone; API `POST /admin/api/users/{id}/notifications`,
  `POST /admin/api/notifications/broadcast`, `GET /admin/api/users/{id}/notifications`.
- Rewards are checked against the economy catalog when sent, so an unpayable reward is rejected early.

## Module layout

`modules/<Name>/<Name>.dll` — the folder name must equal the DLL name (the publish output of the module project).
The module project references `GameServer.Abstractions` with `Private=false` so the host's copy is the only one.

## Hooks

A module can run code before or after any built-in action, for example to refuse a chat message or to give a gift after login.

```csharp
init.RegisterBefore(HookOps.ChatSend, (ctx, requestJson) => Task.FromResult(requestJson)); // may change the request or throw GameException
init.RegisterAfter(HookOps.AuthLogin, async (ctx, request, response) => { /* ... */ });
```

Names are in `HookOps`: `auth.login` (after only), `account.update|delete`, `friends.*`, `groups.*`, `parties.*`, `chat.join|send`, and `rpc:<id>` for RPCs. Requests and responses are camelCase JSON strings. An error in an after hook is logged and ignored.

## Friends, clans/groups, parties

- `/v1/friends` (GET list `?state=`, POST add or accept, DELETE `/{userId}`, POST/DELETE `/{userId}/block`)
- `/v1/groups` create, search, `/mine`, get/update/delete, `/{id}/join|leave`, `/{id}/members`, invite, accept, kick, ban, promote, demote, transfer. "Clan" and "group" are the same thing: `type` picks the limits from the config key `social`, e.g. `{"groupTypes":{"clan":{"maxPerUser":1,"maxMembers":50}}}`.
- `/v1/parties` create, `/mine`, join, leave, invite, kick, promote, close. Parties live in memory (one server instance only) and need an open realtime connection.

## Chat and realtime

Which protocols the server speaks is decided when it is built:

- config: `GameServer:Realtime:Transports` (env `GameServer__Realtime__Transports`, `.env`: `REALTIME_TRANSPORTS`), e.g. `websocket` or `websocket,tcp`
- or code: `AddGameServerCore(config, isDev, realtime: rt => rt.Use("websocket", "tcp"))`
- **No protocol is built into the core.** Each one is a separate project that you add to the Host and register in `Program.cs`: `GameServer.Transport.WebSocket` (`rt.AddWebSocket()`), `GameServer.Transport.Tcp` (`rt.AddTcp()`). To drop one, remove its line and its `ProjectReference`. If the config names a transport that no plugin provides (or none is enabled) the server refuses to start and says which one is missing.
- a new protocol: implement `ITransport` (only framing: receive text frames, call `IConnectionHandler`), then `rt.Add<MyTransport>("mine").Use("mine")`. Chat, friends and parties do not change.

`websocket` listens on `/ws` (use wss by putting TLS in front or enabling HTTPS in Kestrel). `tcp` uses length-prefixed frames on port 7351, with TLS if `Tcp:CertificatePath` is set. `GET /v1/realtime` lists what is enabled.

Ready-made requests for all of this are in `postman/GameServer-social.http`.

Messages are JSON `{ "type", "id", "data" }`. After connecting send `auth` with the JWT within 10 seconds. Client to server: `ping`, `channel.join`, `channel.leave`, `channel.send`, `channel.history`. Server to client: `auth.ok`, `pong`, `channel.joined|left|sent|history|message`, `error`, plus events such as `friend.request`, `group.invite`, `party.update`. Channels: `room:<name>`, `dm:<userId>`, `group:<id>`, `party:<id>`. Modules can send their own events with `ctx.Chat.PushAsync(userId, "game.xyz", data)`.

### Admin panel and admin API for these features

Open `/admin` and use the tabs **Groups**, **Chat**, **Online** and **Social rules**; the Users tab also shows each player's friends, groups and chat mute. The same actions exist under `/admin/api` (header `X-Admin-Key`):

- `GET/PUT/DELETE /groups/{id}`, `GET /groups?q=&type=`, `POST /groups/{id}/members/{userId}/kick|ban`, `POST /groups/{id}/transfer/{userId}`
- `GET /users/{id}/friends`, `DELETE /users/{id}/friends/{otherId}`, `GET /users/{id}/groups`
- `GET /chat/channels`, `GET /chat/messages?channel=`, `DELETE /chat/messages/{id}`, `POST /chat/send` (system message)
- `GET /chat/mutes`, `POST /users/{id}/mute` `{ "minutes": 60, "reason": "spam" }`, `DELETE /users/{id}/mute`. A muted player gets error `muted` when sending.
- `GET /online`, `POST /users/{id}/disconnect`

## Matchmaking and matches (any protocol per game mode)

A **match** is one running game session. Its rules live in a `MatchHandler` class inside a module; the server calls the handler one event at a time (`OnJoin`, `OnMessage`, `OnTick`, `OnLeave`...), so no locking is needed. The **matchmaker** puts waiting players (or whole parties) into matches. **The protocol is separate from the game logic**: every mode picks its own transport.

Modes are defined in the config key `matches` (Config tab in the admin panel):

```json
{ "modes": {
    "duel":  { "handler": "echo", "transport": "udp", "minPlayers": 2, "maxPlayers": 2, "tickRate": 20,
               "rules": { "region": "equal", "skill": { "range": 100, "expandPerSecond": 10 } } },
    "cards": { "handler": "relay", "transport": "websocket", "minPlayers": 2, "maxPlayers": 4, "allowClientCreate": true }
} }
```

Optional per mode: `fillSeconds` (default 5: after this wait a match may start with fewer than `maxPlayers`), `ticketTimeoutSeconds` (120), `joinTimeoutSeconds` (30), `emptyTimeoutSeconds` (30), `matchmaking` (true), `allowClientCreate` (false). `relay` is a built-in handler that forwards each message to the other players with the sender's slot as the first byte; other handlers are registered in a module with `init.RegisterMatchHandler("name", () => new MyMatch())` (see `EchoMatch` in the sample module).

**Protocols.** Which ones are switched on: `GameServer__Match__Transports` (`MATCH_TRANSPORTS` in `.env`), e.g. `websocket,udp`. The first is the default for modes without `transport`. Plugin `GameServer.Transport.WebSocket` (`mt.AddWebSocket()`) provides `websocket` (`ws://host/match?token=...`, binary frames). Plugin `GameServer.Transport.LiteNetLib` (`mt.AddLiteNetLib()`) adds `udp` (LiteNetLib 1.3.5, port 7360 by default, see `GameServer:Match:Udp`): the client connects with LiteNetLib and sends the join token as a string in the connection request data (`NetDataWriter.Put(token)`); use a LiteNetLib 1.x version on the client. Messages are raw bytes; handlers choose `Reliable`, `Unreliable` or `Sequenced` per send (WebSocket sends everything reliably).

**Adding a protocol** (QUIC, raw TCP, Steam relay...): implement `IMatchTransport` (about 50 lines: accept connections, call `host.OnConnectedAsync(conn, token)`, `host.OnMessageAsync`, `host.OnDisconnectedAsync`), register it with `match: mt => mt.Add<MyTransport>("quic")` in `AddGameServerCore` and name it in `GameServer__Match__Transports`. Nothing else changes: tokens, handlers, matchmaker and the admin panel work for it automatically. `LiteNetLibTransport.cs` is the reference.

**Flow for a client:** connect to the realtime socket and sign in; `POST /v1/matchmaker/tickets` `{ "mode": "duel", "properties": { "region": "eu", "skill": 1200 }, "partyId": null }` (or realtime `matchmaker.add`); when a match is found the realtime socket pushes `matchmaker.matched` with `{ matchId, mode, transport, token, endpoint, players }`; connect with that transport within 30 seconds, presenting the token. Other calls: `GET /v1/match/modes`, `DELETE /v1/matchmaker/tickets/{id}`, `POST /v1/matches` (only modes with `allowClientCreate`), `GET /v1/matches?mode=`, `POST /v1/matches/{id}/join` (returns a token), and the realtime messages `match.create`, `match.join`, `match.list`, `matchmaker.add|remove`. A party leader queues the whole party with `partyId`. A ticket is dropped when its owner goes offline (`matchmaker.expired`). Hook names: `matchmaker.add`, `matches.create`, `matches.join`. Modules can create matches with `ctx.Matches.CreateAsync(...)`. Admin: tab **Matches**, `GET /admin/api/matches`, `DELETE /admin/api/matches/{id}`.

## Stats, achievements, quests, daily reward

A game only reports **stats** (counters); achievements and quests are defined in config and measured against them. Every change is counted for the lifetime and for the current day, week and month (UTC).

- Report from a module: `await ctx.Progress.AddStatAsync(userId, "matches_won")` (adds), `ctx.Progress.SetStatMaxAsync(userId, "best_score", 4200)` (only raises). Stat names look like other names: lowercase letters, digits, `_`, `-`, `.`.
- **Achievements** (config key `achievements`, lifetime counters) and **quests** (key `quests`, counted inside `period` `day`/`week`/`month`, claimable again in every new period):

```json
{ "achievements": { "win_10": { "name": "Ten wins", "stat": "matches_won", "target": 10,
    "reward": { "wallet": { "gems": 20 } }, "category": "pvp", "hidden": false, "sort": 1 } } }
{ "quests": { "play_3": { "name": "Play 3 matches", "stat": "matches_played", "target": 3, "period": "day",
    "reward": { "wallet": { "coins": 100 }, "items": { "ticket": 1 } } } } }
```

  `GET /v1/achievements`, `GET /v1/quests` (progress, `completed`, `claimed`, `resetsUtc`), `POST .../{id}/claim` (reward is paid once; 409 `goal_not_complete` / `already_claimed`), `GET /v1/stats`. Hidden achievements appear once reached. When a goal is reached, online players get the realtime event `goal.completed` `{ kind, id, name, hasReward }`.
- **Daily reward** (config key `daily_rewards`): the reward of each day of a login streak; after the last one the cycle starts again.

```json
{ "utcOffsetMinutes": 210, "resetOnMiss": true,
  "rewards": [ { "wallet": { "coins": 100 } }, { "wallet": { "coins": 150 } }, { "items": { "chest": 1 } } ] }
```

  `GET /v1/daily` (streak, `claimedToday`, `nextDay`, `nextClaimUtc`) and `POST /v1/daily/claim` (409 `daily_already_claimed`). `utcOffsetMinutes` moves the start of the day (210 = Iran); `resetOnMiss: false` keeps the streak when a day is skipped.
- Hook names `daily.claim`, `achievements.claim`, `quests.claim`. Admin: tab **Progress**, `GET /admin/api/progress`, `GET /admin/api/users/{id}/progress`, `POST /admin/api/users/{id}/stats` `{ "stat", "value", "mode": "add"|"max" }`.
- Quests are a fixed list; picking random quests per player per day is not built.

## Shop (in-game currency)

Players buy with currencies and items from their wallet. The catalog is the config key `shop` (Config tab); prices are always read from it on the server, never from the client. A purchase is one atomic economy transaction (history reason `shop_purchase`), so it either happens completely or not at all.

```json
{ "products": { "gems_small": {
    "name": "Small gem pack", "category": "gems", "sort": 1,
    "cost":  { "wallet": { "coins": 500 }, "items": { "ticket": 1 } },
    "gives": { "wallet": { "gems": 50 },  "items": { "card_fire": 1 } },
    "limit": { "per": "day", "count": 3 },
    "startUtc": "2026-12-01T00:00:00Z", "endUtc": "2026-12-08T00:00:00Z", "enabled": true } } }
```

Only `gives` is required; no `cost` means free (a daily gift: `limit` `day` 1). `limit.per` is `day`, `week`, `month` (UTC) or `lifetime`. `startUtc`/`endUtc` make a time-limited offer; products that already ended are hidden.

- `GET /v1/shop?category=` lists products with `remaining` (what this player can still buy) and `active`.
- `POST /v1/shop/{id}/buy` `{ "quantity": 1, "requestId": "..." }`. Send a fresh `requestId` per tap and reuse it when retrying after a lost response: the purchase is applied once (`alreadyApplied: true` on the retry). Errors: 404 `product_not_found`, 409 `product_unavailable`, `purchase_limit_reached`, `insufficient_funds`, `insufficient_items`.
- Hook name `shop.buy` (before: refuse or change; after: e.g. an achievement). Admin: tab **Shop**, `GET /admin/api/shop`, `GET /admin/api/users/{id}/shop`.
- Buying with real money (Bazaar, Myket, Google Play) is a separate feature that is not built yet; it will credit the same wallet.

## Tank Arena (demo game that uses everything)

Open <http://localhost:5000/arena/> (second player: `/arena/?p=2` in another tab). It is a 1v1 tank game in the browser:
login, wallet, shop (tank skins), daily reward, quests, achievements, leaderboards, a tournament, friends, clans, chat,
parties, the matchmaker and a server-side match.

1. On the login page press **Write game config** once (needs the admin key). It reads `wwwroot/arena/arena-config.json`
   and saves the match modes (`arena`, `arena_friendly`), shop, quests, achievements, leaderboards and the tournament.
2. Press **Play**, then **Find match** in two tabs (or use a private match code).

The game rules run on the server: `samples/HelloGame.Modules/Arena/ArenaMatch.cs` is a `MatchHandler` that moves tanks and
bullets, decides hits and the winner, pays coins and records stats and leaderboards. Clients only send their input, so a win
cannot be faked. A handler reaches the economy, progress, leaderboards, notifications and config through `match.Services`.
It also shows reconnecting (a dropped player can rejoin within 15 s with `POST /v1/matches/{id}/join`) and different
delivery modes (`Sequenced` snapshots; they become real UDP-style messages on a UDP transport).

**Bots:** `dotnet run --project tests/GameServer.ArenaBots -- http://localhost:5000 <adminKey> 8` creates 8 players, plays four
full matches through the matchmaker and checks coins, stats, quests, achievements, leaderboards and the shop afterwards
(`arena-bots.log`). Raise the number to load-test (up to 200). `run-tests` runs it with 8 bots.

## Web playground

Open <http://localhost:5000/playground/> for a browser client: a guided tutorial, every player endpoint
documented with a live "Send" button (auto token refresh), a realtime websocket console and a match console.
It is a single static file in `src/GameServer.Host/wwwroot/playground/`; delete the folder to remove it in production.

## Unity SDK

`sdk/unity/` has a Unity client package (`com.gameserver.client`) covering REST, realtime (ws/tcp),
matchmaking and matches (ws/UDP). See `sdk/unity/README.md`. `run-tests` also builds and runs
`sdk/unity/SdkSmokeTest` against the live server.

## Automatic test run

`run-tests.ps1` (Windows) or `run-tests.sh` builds the solution (also the SDK test and the Arena bots), starts Postgres with Docker, publishes the sample module, starts the server, runs `tests/GameServer.SmokeTest` (login, hooks, friends, clans, chat over websocket and tcp, parties, matchmaker and matches over websocket, shop, stats/achievements/quests/daily reward, live notification event, admin API, admin console (roles, audit, dashboard, storage, live-ops, modules), mute, rate limit, account deletion) and writes everything into `logs/REPORT.txt`.

```
powershell -ExecutionPolicy Bypass -File .\run-tests.ps1
```

The test can also be run by hand against a running server: `dotnet run --project tests/GameServer.SmokeTest -- http://localhost:5000 <adminKey>` (log in `smoke-test.log`). It creates three throw-away players and deletes them at the end.

## Not done yet

- Apple, CrazyGames and email login providers
- A real SMS gateway (phone login is fully built but needs an `ISmsSender`)
- Module-owned tables/migrations
- Purchase verification with real money, Unity client SDK
- Matches run on the instance that created them (no multi-server routing yet); UDP is not covered by the smoke test (use a LiteNetLib client)
- Parties across several server instances (they are in memory), a Redis/Postgres backplane for chat fan-out
- The code of hooks, friends, groups, parties, chat, matchmaking and matches was written without a compiler: run `dotnet build` and fix anything it reports

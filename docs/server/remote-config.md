# Remote config

Game content lives in the database as **config keys**: free-form JSON per key, either public or server-only. A game ships its
starting values as files in its module, which the server writes into the database at startup, see
[Game config files](./game-config). After that you can edit the keys in the admin panel (**Config** tab) or with the admin API
(the **Export as files (zip)** button turns the current database values back into files):

```
PUT /admin/api/config/{key}     { "value": <any JSON>, "public": true }
GET /admin/api/config/export    zip with one <key>.json per key
```

Each change keeps the previous value (last 50 per key) so you can roll back:
`GET /admin/api/config/{key}/history`, `POST /admin/api/config/{key}/rollback/{historyId}`. `DELETE /admin/api/config/{key}`
removes a key (the old value goes to the history too).

## Reading config

- **Clients**: `GET /v1/config` needs no login and returns all **public** keys as `{ "config": { key: value } }`
  with ETag / `If-None-Match` (answer `304`). Non-public keys never leave the server.
- **Modules**: `await ctx.Config.GetAsync<T>("key")`. Any key, camelCase and case-insensitive, cached about 5 seconds.

Changes apply within about 5 seconds, per server instance (a write on the same instance shows up at once). In `sync` mode of the game config files, a key that has a file is reset to the file's value at every restart.

## Version check

Config key `app_version`:

```json
{ "default":    { "latest": "1.2.0", "minimum": "1.0.0", "url": "https://example.com/get" },
  "bazaar":     { "latest": "1.2.0", "minimum": "1.1.0", "url": "https://cafebazaar.ir/app/..." },
  "googleplay": { "latest": "1.2.0", "minimum": "1.0.0", "url": "https://play.google.com/store/apps/details?id=..." } }
```

`GET /v1/version-check?store=bazaar&version=1.2.0` answers
`{ status: ok | update_available | update_required, latest, minimum, url }`. A `version` that is not like `1.2` or
`1.2.3` is `400 invalid_version`.
An unknown store falls back to `default`; with no rule at all the answer is `ok`.

## Maintenance mode

Config key `maintenance` (always public):

```json
{ "enabled": true, "message": "Back at 10:00", "until": "2026-12-01T10:00:00Z" }
```

While it is enabled, every `/v1/*` call except `/v1/config` and `/v1/version-check` answers
`503 { error: "maintenance", message, until }`. The admin API and panel keep working. The panel has a one-click switch.

## A typical client start

`version-check` -> `config` -> login. The sample game's Boot screen does exactly this (force-update and soft-update popups,
maintenance screen, then sign-in).

## Keys the server understands

`app_version`, `maintenance`, `economy`, `shop`, `achievements`, `quests`, `daily_rewards`, `leaderboards`, `tournaments`,
`matches`, `social`. Details are in [Config keys](../reference/config-keys). You can add any other key for your own module.

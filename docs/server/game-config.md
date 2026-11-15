# Game config files

A game's config (shop, quests, match modes, leaderboards...) ships **with the game module** as JSON files, so it lives in git next
to the module's code. At startup the server checks the files and writes them into the database. The database is still where the
config is read from at runtime, so the admin panel, [Remote config](./remote-config) and the clients work as before.

```
modules/
  MyGame.Modules/
    MyGame.Modules.dll
    config/
      matches.json
      shop.json
      quests.json
      ...
```

In the module project the folder is `config/` next to the `.csproj`. Add one item so `dotnet publish` copies it:

```xml
<ItemGroup>
  <None Include="config\*.json" CopyToPublishDirectory="PreserveNewest" LinkBase="config" />
</ItemGroup>
```

The sample (`samples/HelloGame.Modules`) does exactly this. After `dotnet publish MyGame.Modules -c Release -o modules/MyGame.Modules`
the files are in `modules/MyGame.Modules/config/`.

## File format

One file per config key. The file name (without `.json`) is the key, so it must be a valid key: lowercase letters, digits, `_`,
`-` or `.`, starting with a letter or digit, at most 64 characters.

```json
{
  "$schema": "optional, ignored for now",
  "public": false,
  "value": { "modes": { "arena": { "handler": "arena", "minPlayers": 2, "maxPlayers": 2 } } }
}
```

| Field | Meaning |
|---|---|
| `value` | Required. The JSON the key holds. Same shape as in the admin panel |
| `public` | `true` = sent to every client by `GET /v1/config`. Default `false`. The key `maintenance` is always public |
| `$schema` | Optional. Ignored by the server (reserved for editor autocomplete) |
| `_anything` | Any field starting with `_` is ignored, use it for a comment: `"_note": "tuned for launch"` |

Other details:

- `//` comments and trailing commas are allowed when the server reads a file.
- A file whose name starts with `_` (for example `_notes.json`) is skipped. Other file types and sub-folders are ignored.
- A field the format does not know (`"pubic": true`) is an error, so a typo does not silently change the meaning.
- Dates in the files are plain UTC strings such as `"2026-01-01T00:00:00Z"`. Use fixed dates. A tournament has a start and an end, so
  pick ones far enough away (the sample uses 2026 to 2030) and change them when you run a real event.

## Where files come from

1. `modules/<Name>/config/*.json` of every loaded module.
2. Optionally one extra folder, `GameServer:GameConfig:Path` (env `GameServer__GameConfig__Path`), for config that ops manages
   outside the module. A relative path is resolved from the host's content root (see [Settings](../reference/settings#game-config)).

The same key in two places stops the server at startup and names both files. There is no priority between sources, on purpose.

## Modes

`GameServer:GameConfig:Mode` (env `GameServer__GameConfig__Mode`, in Docker Compose `GAME_CONFIG_MODE` in `.env`):

| Mode | What happens at startup |
|---|---|
| `seed` (default, also when empty) | A key is written **only if it is missing in the database**. Edits made in the admin panel stay |
| `sync` | The files are the source of truth. A key whose value or `public` flag differs from the database is written. Keys without a file are never deleted |
| `off` | The files are ignored (not even read) |

Which one to use:

- **Development and a first deploy**: `seed`. A fresh database gets the whole game config, nothing else changes.
- **Production where git is the source of truth**: `sync`. What is in the module is what the server runs after every restart.
- **Production where the team tunes the game in the admin panel**: `seed`, and use the export below now and then to commit the result.

::: warning In `sync` mode panel edits are overwritten
If a key has a file, an edit made in the admin panel lasts only until the next restart. Edit the file instead, or keep that key out
of the module. Keys that have no file (for example `maintenance`, which you switch from the panel) are never touched.
:::

Writes made at startup go through the normal config store, so they appear like any other change: the old value is kept in the
key's history (changed by `config-files`, you can roll back from the panel) and a line in the admin audit log. The server logs one
line per key, `written` or `skipped` with the reason.

## Checked at startup

Before the server touches the database it reads every file and validates it the same way a write through the admin API is
validated: the key name, the 64 KB limit, and for the known keys the same parsers the services use (the shop catalog, the quest
and achievement goals, match modes, economy, daily rewards, leaderboards, tournaments, social rules). A mistake stops the server
with the file path and the reason, all problems at once:

```
Game config files are invalid:
  /opt/gameserver/modules/MyGame.Modules/config/shop.json: shop.products.daily_crate.gives is required.
  /opt/gameserver/modules/MyGame.Modules/config/leaderboards.json: not valid JSON ('n' is an invalid start of a property name...)
```

So a typo shows up on `docker compose up`, not when the first player opens the shop. What this cannot check: references between keys
(a quest that names a stat nobody reports, a shop product that gives an item that is not in `economy`).

The order at startup is: load modules, read and validate the config files, run the database migrations, apply the files.

## Export from a running server

To move config that exists only in a database into files, the admin API returns it in exactly this format:

```
GET /admin/api/config/export        header X-Admin-Key
```

The answer is `game-config.zip` with one `<key>.json` per key (public and server-only). In the admin panel it is the **Export as
files (zip)** button in the **Config** tab. Any admin role can download it, like `GET /admin/api/config`, so treat the file as secret
when server-only keys hold secrets.

## Moving an existing server to files

1. Download the zip from the panel (or `curl -H "X-Admin-Key: ..." -o game-config.zip https://your-server/admin/api/config/export`).
2. Unzip it into `config/` of your module project, add the `<None Include="config\*.json" ... />` item, and commit.
3. Delete the keys that are not game content (for example `maintenance`, `app_version`) from the folder if you want to keep
   switching them in the panel. Or keep them and live with `sync` semantics for them.
4. Publish the module and restart the server. In `seed` mode nothing changes yet (every key exists).
5. Set `GAME_CONFIG_MODE=sync` (or `GameServer__GameConfig__Mode=sync`) and restart. The log shows every key as `skipped, unchanged`
   when the files match the database. From now on a deploy is `git pull`, publish, restart.

## Cache

The server keeps all config keys in memory for about 5 seconds, so the shop, quests and match modes do not query the database on
every request. Every write through the store (the admin API, rollback, and the startup apply above) drops that copy at once. This
cache is per server instance; a second instance would see a change only after the 5 seconds (the server is single-instance, see
[Production](./production#_6-know-the-limits)).

## Reference: one example per key

All examples are shortened from `samples/HelloGame.Modules/config/`. The field lists are in [Config keys](../reference/config-keys);
the pages linked there explain each one.

### `matches` ([Matches](./matches))

```json
{ "public": false, "value": { "modes": {
  "arena": { "handler": "arena", "transport": "websocket", "minPlayers": 2, "maxPlayers": 2, "tickRate": 20,
             "matchmaking": true, "fillSeconds": 3 } } } }
```

### `leaderboards` ([Leaderboards](./leaderboards))

```json
{ "public": false, "value": {
  "arena_wins": { "sort": "desc", "operator": "incr", "reset": "never" },
  "arena_weekly": { "sort": "desc", "operator": "incr", "reset": "weekly" } } }
```

### `tournaments` ([Leaderboards](./leaderboards))

```json
{ "public": false, "value": { "arena_cup": {
  "name": { "en": "Arena Cup" }, "start": "2026-01-01T00:00:00Z", "end": "2030-12-31T23:59:59Z", "operator": "incr",
  "entryFee": { "coins": 10 },
  "rewards": [ { "fromRank": 1, "toRank": 1, "wallet": { "coins": 300, "gems": 10 } } ] } } }
```

### `shop` ([Shop](./shop))

```json
{ "public": false, "value": { "products": {
  "skin_red": { "name": "Red tank", "category": "skins", "sort": 2, "cost": { "wallet": { "coins": 60 } },
                "gives": { "items": { "skin_red": 1 } }, "limit": { "per": "lifetime", "count": 1 } } } } }
```

### `achievements` and `quests` ([Progress and quests](./progress))

```json
{ "public": false, "value": { "achievements": {
  "first_win": { "name": "First victory", "stat": "matches_won", "target": 1, "reward": { "wallet": { "coins": 50 } } } } } }
```

```json
{ "public": false, "value": { "quests": {
  "play_3": { "name": "Play 3 matches today", "stat": "matches_played", "target": 3, "period": "day",
              "reward": { "wallet": { "coins": 40 } } } } } }
```

### `daily_rewards` ([Progress and quests](./progress#daily-reward))

```json
{ "public": false, "value": { "utcOffsetMinutes": 0, "resetOnMiss": true,
  "rewards": [ { "wallet": { "coins": 50 } }, { "wallet": { "coins": 75 } }, { "wallet": { "gems": 2 } } ] } }
```

### `economy` ([Wallet and inventory](./economy))

Not in the sample (it has no catalog, so any currency name is allowed). With it, only the listed currencies and items are valid:

```json
{ "public": true, "value": {
  "currencies": { "coins": { "name": "Coins", "max": 999999 }, "gems": { "name": "Gems" } },
  "items": { "skin_red": { "stack": 1 } } } }
```

### `social` ([Friends, clans and parties](./social))

```json
{ "public": false, "value": { "maxFriends": 100, "groupTypes": { "clan": { "maxPerUser": 1, "maxMembers": 50 } } } }
```

### `app_version` ([Remote config](./remote-config#version-check)) and `maintenance`

```json
{ "public": true, "value": { "default": { "latest": "1.2.0", "minimum": "1.0.0", "url": "https://example.com/get" } } }
```

```json
{ "public": true, "value": { "enabled": false } }
```

`maintenance` is a switch you flip at runtime. Keep it out of `sync` unless you want every restart to turn it back to the file's value.

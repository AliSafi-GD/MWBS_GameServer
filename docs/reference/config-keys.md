# Config keys

Remote config keys the server understands. Edit them in the admin panel (Config tab). See [Remote config](../server/remote-config).

| Key | Public? | Used by | Page |
|---|---|---|---|
| `app_version` | yes | Version check | [Remote config](../server/remote-config#version-check) |
| `maintenance` | always | Maintenance mode | [Remote config](../server/remote-config#maintenance-mode) |
| `economy` | optional | Currency and item catalog, limits | [Wallet and inventory](../server/economy) |
| `shop` | yes (so clients can list) | Shop catalog | [Shop](../server/shop) |
| `achievements` | | Lifetime goals | [Progress](../server/progress) |
| `quests` | | Periodic goals | [Progress](../server/progress) |
| `daily_rewards` | | Login streak | [Progress](../server/progress#daily-reward) |
| `leaderboards` | | Boards | [Leaderboards](../server/leaderboards) |
| `tournaments` | | Tournaments | [Leaderboards](../server/leaderboards) |
| `matches` | | Match modes | [Matches](../server/matches) |
| `social` | | Group types and limits | [Social](../server/social) |

Any other key is yours: read it in a module with `ctx.Config.GetAsync<T>("key")`; mark it public to send it to clients too.

## Shapes at a glance

```json
// economy
{ "currencies": { "coins": { "name": "Coins", "max": 999999 } }, "items": { "card_fire": { "stack": 99 } } }

// shop
{ "products": { "id": { "name": "", "category": "", "cost": { "wallet": {}, "items": {} },
  "gives": { "wallet": {}, "items": {} }, "limit": { "per": "day", "count": 3 },
  "startUtc": "", "endUtc": "", "enabled": true } } }

// achievements
{ "win_10": { "name": "", "stat": "matches_won", "target": 10, "reward": { "wallet": {} },
  "category": "", "hidden": false, "sort": 1 } }

// quests
{ "play_3": { "name": "", "stat": "matches_played", "target": 3, "period": "day|week|month", "reward": {} } }

// daily_rewards
{ "utcOffsetMinutes": 210, "resetOnMiss": true, "rewards": [ { "wallet": { "coins": 100 } } ] }

// leaderboards
{ "weekly_score": { "sort": "desc", "operator": "best|set|incr", "reset": "never|daily|weekly|monthly" } }

// matches
{ "modes": { "duel": { "handler": "", "transport": "websocket|udp", "minPlayers": 2, "maxPlayers": 2, "tickRate": 20 } } }

// social
{ "groupTypes": { "clan": { "maxPerUser": 1, "maxMembers": 50 } } }
```

A complete working set is in `src/GameServer.Host/wwwroot/arena/arena-config.json`.

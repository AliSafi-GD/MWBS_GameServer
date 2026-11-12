# Progress and quests

A game only reports **stats** (counters). Achievements and quests are defined in config and measured against those stats.
Every change is counted for the lifetime and for the current day, week and month (UTC).

## Reporting stats

From a module or a match handler:

```csharp
await ctx.Progress.AddStatAsync(userId, "matches_won");          // adds 1
await ctx.Progress.SetStatMaxAsync(userId, "best_score", 4200);  // only raises
```

Stat names are lowercase letters, digits, `_`, `-` and `.`.

## Achievements and quests

Achievements (config key `achievements`) count the lifetime. Quests (key `quests`) count inside a `period`
(`day`, `week`, `month`) and can be claimed again in every new period.

```json
{ "achievements": { "win_10": { "name": "Ten wins", "stat": "matches_won", "target": 10,
    "reward": { "wallet": { "gems": 20 } }, "category": "pvp", "hidden": false, "sort": 1 } } }
```

```json
{ "quests": { "play_3": { "name": "Play 3 matches", "stat": "matches_played", "target": 3, "period": "day",
    "reward": { "wallet": { "coins": 100 }, "items": { "ticket": 1 } } } } }
```

A quest with `"stat": "matches_won"` advances when a handler calls `AddStatAsync(user, "matches_won")`.

## API

- `GET /v1/achievements`, `GET /v1/quests`: progress, `completed`, `claimed`, `resetsUtc`
- `POST /v1/achievements/{id}/claim`, `POST /v1/quests/{id}/claim`: the reward is paid once.
  `409 goal_not_complete` / `already_claimed`
- `GET /v1/stats`

Hidden achievements appear once reached. When a goal is reached, online players get the realtime event
`goal.completed` `{ kind, id, name, hasReward }`.

## Daily reward

Config key `daily_rewards`: the reward of each day of a login streak. After the last one the cycle starts again.

```json
{ "utcOffsetMinutes": 210, "resetOnMiss": true,
  "rewards": [ { "wallet": { "coins": 100 } }, { "wallet": { "coins": 150 } }, { "items": { "chest": 1 } } ] }
```

- `GET /v1/daily`: streak, `claimedToday`, `nextDay`, `nextClaimUtc`
- `POST /v1/daily/claim`: `409 daily_already_claimed`
- `utcOffsetMinutes` moves the start of the day (210 = Iran). `resetOnMiss: false` keeps the streak when a day is skipped.

## Hooks and admin

Hook names `daily.claim`, `achievements.claim`, `quests.claim`. Admin tab **Progress**, `GET /admin/api/progress`,
`GET /admin/api/users/{id}/progress`, `POST /admin/api/users/{id}/stats` `{ "stat", "value", "mode": "add"|"max" }`.

::: info Not built
Quests are a fixed list. Picking random quests per player per day is not built.
:::

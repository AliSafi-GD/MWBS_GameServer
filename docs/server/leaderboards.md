# Leaderboards and tournaments

Defined in config (the Config tab has templates), read by clients, **written only by modules**.

```json
"leaderboards": { "weekly_score": { "sort": "desc", "operator": "best", "reset": "weekly" } }
```

```json
"tournaments": { "winter_cup": {
  "name": { "en": "Winter Cup" },
  "start": "2026-12-01T00:00:00Z", "end": "2026-12-08T00:00:00Z",
  "entryFee": { "coins": 50 },
  "rewards": [ { "fromRank": 1, "toRank": 1,  "wallet": { "coins": 1000 } },
               { "fromRank": 2, "toRank": 10, "wallet": { "coins": 200 } } ] } }
```

## Leaderboard rules

- `operator`: `best` keeps the better score, `set` overwrites, `incr` adds.
- `reset`: `never`, `daily`, `weekly`, `monthly` (UTC).
- There is **no reset job**. A new day, week or month is just a new period key (`2026-W46`). Old periods stay readable with `?period=`.
- Ties go to whoever reached the score first. Banned players are hidden.

## Submitting scores (server side)

```csharp
await ctx.Leaderboards.SubmitAsync("weekly_score", ctx.UserId, score);
await ctx.Leaderboards.SubmitTournamentAsync("winter_cup", ctx.UserId, score);   // needs join + running
```

Always validate the score in your module first. The sample RPC `submit_score` shows a simple range check.

## Client API

| Call | What |
|---|---|
| `GET /v1/leaderboards/{id}?limit=50&around=0&period=current` | Top entries plus `me`. `around=N` shows N above and below me |
| `GET /v1/tournaments` | Running tournaments |
| `GET /v1/tournaments/{id}` | Info and standings |
| `POST /v1/tournaments/{id}/join` | Pays the entry fee once |
| `POST /v1/tournaments/{id}/claim` | After the end: the reward for the final rank, once. Needs a score |

Tournament rewards are claimed by the player (no background job), so unclaimed rewards simply stay claimable.

## Admin

**Leaderboards** tab: view a board and remove a cheater. `GET /admin/api/leaderboards/{id}` (use `tournament:<id>` for
tournaments). The **Live-Ops** tab has a tournament editor (extend by 7 days, standings, delete).

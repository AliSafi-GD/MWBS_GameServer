# Leaderboards

`client.Competitions` reads leaderboards and tournaments.

```csharp
Board board = await client.Competitions.LeaderboardAsync("weekly_score", limit: 50, around: 0, period: null);
```

`Board` has the entries and **your rank**. Pass `around: 5` to center the list on the player (5 above and 5 below).
`period` is null for the current period or a key such as `2026-W46` for an old one.

## Tournaments

```csharp
var all  = await client.Competitions.TournamentsAsync();
var one  = await client.Competitions.TournamentAsync("winter_cup", limit: 50, around: 0);

await client.Competitions.JoinTournamentAsync("winter_cup");     // pays the entry fee once
await client.Competitions.ClaimTournamentAsync("winter_cup");    // after the end, for the final rank
```

## Scores are submitted by the server

There is no `Submit` call on the client, on purpose. A cheater would simply send a huge score. Submit scores from an RPC
(after validating) or from a match handler:

```csharp
await ctx.Leaderboards.SubmitAsync("weekly_score", ctx.UserId, score);
```

See [Leaderboards and tournaments](../server/leaderboards).

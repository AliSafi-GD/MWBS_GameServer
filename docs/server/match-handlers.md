# Match handlers

A `MatchHandler` holds the **rules of one match mode**. A new instance is created for every match. The server calls its methods
one at a time, so you never need locks.

## Register

```csharp
public sealed class MyModule : IServerModule
{
    public void Initialize(IModuleInitializer init) => init.RegisterMatchHandler("mygame", () => new MyMatch());
}
```

Then define the mode in the `matches` config (admin panel > Config):

```json
{ "modes": { "mygame": { "handler": "mygame", "transport": "websocket", "minPlayers": 2, "maxPlayers": 4, "tickRate": 20 } } }
```

See [Matchmaking and matches](./matches) for all mode fields.

## The handler

```csharp
public sealed class MyMatch : MatchHandler
{
    public override Task OnCreateAsync(IMatch m) { ... }                           // once, before anybody joins
    public override Task<bool> OnJoinAttemptAsync(IMatch m, MatchPlayer p) => ...; // return false to refuse
    public override Task OnJoinAsync(IMatch m, MatchPlayer p) { ... }              // also after a reconnect
    public override Task OnMessageAsync(IMatch m, MatchPlayer p, ReadOnlyMemory<byte> data) { ... } // validate input here
    public override Task OnTickAsync(IMatch m) { ... }                             // tickRate times per second
    public override Task OnLeaveAsync(IMatch m, MatchPlayer p, MatchLeaveReason r) { ... }
    public override Task OnEndAsync(IMatch m) { ... }
}
```

`MatchLeaveReason`: `Left`, `Disconnected` (the player may reconnect with a new token), `Kicked`, `MatchEnded`.

## `IMatch`

| Member | Use |
|---|---|
| `Id`, `Mode`, `Label`, `Parameters`, `Tick` | Info. `Label` shows in the match list |
| `Players` / `Connected` | Everybody who belongs / who is connected now |
| `State` | Your own per-match object |
| `Services` | `Economy`, `Progress`, `Leaderboards`, `Notifications`, `Config` |
| `SendAsync(player, data, delivery)` | To one player |
| `BroadcastAsync(data, delivery, except)` | To everybody |
| `KickAsync(player, reason)` | Disconnect and block the old token |
| `End(reason)` | End after the current call returns |

`MatchPlayer(UserId, Username, Slot)`: the slot is 0, 1, 2... and stays the same when the player reconnects.

## Delivery

`MatchDelivery.Reliable` (always arrives, in order), `Unreliable` (may be lost: positions), `Sequenced` (older packets than the
newest are dropped). WebSocket delivers everything reliably; a UDP transport honours the difference.

## Paying out

Do it from the handler when the match is decided, so a win cannot be faked:

```csharp
await m.Services.Progress.AddStatAsync(winner.UserId, "matches_won");
await m.Services.Leaderboards.SubmitAsync("weekly_score", winner.UserId, score);
await m.Services.Economy.ApplyAsync(winner.UserId, new EconomyTransaction("match_win", $"win:{m.Id}").Currency("coins", 30));
```

## A complete example

`samples/HelloGame.Modules/Arena/ArenaMatch.cs` is a full server-authoritative 1v1 tank game: clients send only their input
(direction, aim, fire); the handler moves tanks and bullets at 20 ticks per second, decides hits and the winner, pays coins,
records stats and leaderboards, and supports reconnecting within 15 seconds. The message protocol is documented at the top of the file.

`EchoMatch` in `HelloModule.cs` is the smallest possible handler.

## Tips

- Treat every client message as untrusted. Clamp values, rate-limit actions, ignore what is not allowed in the current phase.
- Keep snapshots small and send them with `Sequenced` delivery when on UDP.
- Use idempotency keys with the match id for rewards, so a retry cannot pay twice.

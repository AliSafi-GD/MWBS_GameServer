# Matchmaking and matches

A match is one running game session. Its rules run **on the server** in a [`MatchHandler`](../server/match-handlers). The client
connects and exchanges raw bytes with it. The message format is whatever your client and handler agree on (JSON or binary).

## Find a match

```csharp
var modes = await client.Matches.ModesAsync();       // what the server offers: names, min/max players, transport

client.Realtime.MatchFound += async found => {
    var conn = await client.Matches.ConnectAsync(found);      // picks websocket or udp from the mode
    conn.DataReceived += data => HandleServerMessage(data);   // raw bytes from the handler
    conn.Closed       += reason => BackToLobby(reason);
    await conn.SendJsonAsync(new { t = "hello" });            // also SendAsync(byte[], delivery) / SendTextAsync
};

var ticket = await client.Realtime.AddToMatchmakerAsync("duel",
    new Dictionary<string, object> { { "region", "eu" } });

// cancel:
await client.Realtime.RemoveFromMatchmakerAsync(ticket.Id);
```

- Connect within about **30 seconds** of `MatchFound` or the seat is lost.
- `MatchmakerExpired` fires when a ticket times out or you went offline.
- A party leader queues the whole party: `AddToMatchmakerAsync(mode, props, partyId)`.

## Private rooms

For modes with `allowClientCreate`:

```csharp
var created = await client.Matches.CreateAsync("arena_friendly", label: "Ali's room");
var list    = await client.Matches.ListAsync("arena_friendly");
var info    = await client.Matches.JoinAsync(matchId);          // returns what you need to connect
var conn    = await client.Matches.ConnectAsync(info);
```

## Reconnect

If the connection drops mid-match, call `JoinAsync(matchId)` then `ConnectAsync(info)`. You have about 15 seconds in the
sample game (a handler decides). Show a clear state while you try.

## Sending

```csharp
await conn.SendAsync(bytes, MatchDelivery.Unreliable);
await conn.SendJsonAsync(new { dx = 1, dy = 0, fire = true });
await conn.CloseAsync();
```

| Delivery | Use for |
|---|---|
| `Reliable` (default) | Anything that must arrive, in order |
| `Unreliable` | Per-tick input and positions |
| `Sequenced` | Snapshots (older packets than the newest are dropped) |

WebSocket treats all of them as reliable. Only UDP honours the difference.

## The `relay` handler

The built-in `relay` handler forwards each message to the other players with a leading slot byte:

```csharp
if (RelayMessage.TryParse(data, out int slot, out byte[] payload)) { ... }
```

Use it only for turn-based or casual games. For anything competitive write a real handler.

## Architecture

- Decode server snapshots into a state object and render from it (interpolate between snapshots).
- Send only **input**, never results.
- Test with two clients (two editor instances, or a build plus the editor).
- Kill the server mid-match, turn off the network, and relaunch: each must end in a clear UI state, not a hang.

The complete reference is the Tank Arena `Arena/ArenaScreen.cs` and `Core/Services/PlayService.cs`.

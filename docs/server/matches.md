# Matchmaking and matches

A **match** is one running game session. Its rules live in a `MatchHandler` class inside a module. The server calls the handler
one event at a time (`OnJoin`, `OnMessage`, `OnTick`, `OnLeave`...), so **no locking is needed**.

The **matchmaker** puts waiting players (or whole parties) into matches.

The protocol is separate from the game logic: **every mode picks its own transport**.

## Defining modes

Modes live in the config key `matches` (Config tab):

```json
{ "modes": {
    "duel":  { "handler": "echo", "transport": "udp", "minPlayers": 2, "maxPlayers": 2, "tickRate": 20,
               "rules": { "region": "equal", "skill": { "range": 100, "expandPerSecond": 10 } } },
    "cards": { "handler": "relay", "transport": "websocket", "minPlayers": 2, "maxPlayers": 4,
               "allowClientCreate": true }
} }
```

| Field | Default | Meaning |
|---|---|---|
| `handler` | the mode name | Name registered with `RegisterMatchHandler`. `relay` is built in |
| `transport` | first enabled | `websocket`, `udp`... |
| `minPlayers`, `maxPlayers` | | Match size |
| `tickRate` | none | Calls to `OnTickAsync` per second |
| `fillSeconds` | 5 | After this wait a match may start with fewer than `maxPlayers` |
| `ticketTimeoutSeconds` | 120 | A waiting ticket expires |
| `joinTimeoutSeconds` | 30 | Time to connect after the match is found |
| `emptyTimeoutSeconds` | 30 | An empty match is closed |
| `matchmaking` | true | Allow the matchmaker for this mode |
| `allowClientCreate` | false | Players may create private rooms |
| `rules` | | Matching rules (region, skill range that widens over time) |

`relay` forwards each message to the other players with the sender's slot as the first byte. Use it for turn-based or casual
games; for anything competitive write a [real handler](./match-handlers).

## Protocols

Enabled with `GameServer__Match__Transports` (`MATCH_TRANSPORTS` in `.env`), e.g. `websocket,udp`. The first is the default
for modes without `transport`.

| Plugin | Registration | Notes |
|---|---|---|
| `GameServer.Transport.WebSocket` | `mt.AddWebSocket()` | `ws://host/match?token=...`, binary frames. Everything is reliable |
| `GameServer.Transport.LiteNetLib` | `mt.AddLiteNetLib()` | `udp`, LiteNetLib 1.3.5, port 7360 (`GameServer:Match:Udp`). Handlers choose `Reliable`, `Unreliable` or `Sequenced` per send |

For UDP the client connects with LiteNetLib and sends the join token as a string in the connection request data
(`NetDataWriter.Put(token)`). Use a LiteNetLib **1.x** version on the client. For a public server set
`GameServer:Match:Udp:PublicHost`.

### Adding a protocol

QUIC, raw TCP, Steam relay: implement `IMatchTransport` (about 50 lines: accept connections, call
`host.OnConnectedAsync(conn, token)`, `host.OnMessageAsync`, `host.OnDisconnectedAsync`), register it with
`match: mt => mt.Add<MyTransport>("quic")` in `AddGameServerCore`, and name it in `GameServer__Match__Transports`.
Tokens, handlers, matchmaker and the admin panel work for it automatically. `LiteNetLibTransport.cs` is the reference.

## Flow for a client

1. Connect to the realtime socket and sign in.
2. `POST /v1/matchmaker/tickets` `{ "mode": "duel", "properties": { "region": "eu", "skill": 1200 }, "partyId": null }`
   (or the realtime message `matchmaker.add`).
3. When a match is found, the realtime socket pushes `matchmaker.matched` with
   `{ matchId, mode, transport, token, endpoint, players }`.
4. Connect with that transport **within 30 seconds**, presenting the token.

Other calls: `GET /v1/match/modes`, `DELETE /v1/matchmaker/tickets/{id}`, `POST /v1/matches` (only modes with
`allowClientCreate`), `GET /v1/matches?mode=`, `POST /v1/matches/{id}/join` (returns a token), and the realtime messages
`match.create`, `match.join`, `match.list`, `matchmaker.add|remove`.

A party leader queues the whole party with `partyId`. A ticket is dropped when its owner goes offline
(`matchmaker.expired`). Hook names: `matchmaker.add`, `matches.create`, `matches.join`.
A module can create matches with `ctx.Matches.CreateAsync(...)`.

::: info Limits
Matches run on the instance that created them (no multi-server routing yet). UDP is not covered by the smoke test.
:::

Admin: tab **Matches**, `GET /admin/api/matches`, `DELETE /admin/api/matches/{id}`.

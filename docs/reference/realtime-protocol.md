# Realtime protocol

Use this page only if you write your own client. The Unity SDK implements all of it.

## Transports

| Transport | Address |
|---|---|
| WebSocket | `ws://host/ws` (`wss://` behind TLS) |
| TCP | host port 7351, length-prefixed frames, TLS if `Tcp:CertificatePath` is set |

## Frame

Every message is JSON:

```json
{ "type": "channel.send", "id": "42", "data": { "channel": "room:lobby", "text": "hello" } }
```

`id` correlates a request with its answer.

## Handshake

1. Open the connection.
2. Within **10 seconds** send `auth` with the JWT.
3. The server answers `auth.ok`. Send `ping` regularly (the server drops silent connections after about two minutes); it answers `pong`.

## Client to server

| type | Purpose |
|---|---|
| `auth` | Sign in with the JWT |
| `ping` | Keep alive |
| `channel.join`, `channel.leave` | Join or leave a channel |
| `channel.send` | Send a chat message |
| `channel.history` | Read history |
| `matchmaker.add`, `matchmaker.remove` | Queue, cancel |
| `match.create`, `match.join`, `match.list` | Private rooms |

## Server to client

| type | Meaning |
|---|---|
| `auth.ok`, `pong`, `error` | Connection |
| `channel.joined`, `channel.left`, `channel.sent`, `channel.history`, `channel.message` | Chat |
| `friend.request`, `group.invite`, `party.update` | Social |
| `notification.new` | `{ id, broadcast, subject, hasReward }` |
| `goal.completed` | `{ kind, id, name, hasReward }` |
| `matchmaker.matched` | `{ matchId, mode, transport, token, endpoint, players }` |
| `matchmaker.expired` | Ticket dropped |
| any `game.*` | Your own events from `ctx.Chat.PushAsync` |

## Channels

`room:<name>`, `dm:<userId>`, `group:<id>`, `party:<id>`.

## Limits

`MaxMessageBytes` 16384, `MaxConnectionsPerUser` 5, `AuthTimeoutSeconds` 10, `IdleTimeoutSeconds` 120
(all under `GameServer:Realtime`).

## Match connections

A match uses its own transport. Connect within `TokenSeconds` (30) of `matchmaker.matched`.

- WebSocket: `ws://host/match?token=<token>`, binary frames.
- UDP (LiteNetLib 1.x): put the token in the connection request as a string (`NetDataWriter.Put(token)`); default port 7360.

# Chat and realtime

The realtime connection carries chat, party and friend events, notifications and matchmaker results.

## Choosing the protocol

Which protocols the server speaks is decided when it is built or started:

- config: `GameServer:Realtime:Transports` (env `GameServer__Realtime__Transports`, `.env`: `REALTIME_TRANSPORTS`), e.g. `websocket` or `websocket,tcp`
- or code: `AddGameServerCore(config, isDev, realtime: rt => rt.Use("websocket", "tcp"))`

**No protocol is built into the core.** Each one is a separate project that you add to the Host and register in `Program.cs`:

| Plugin | Registration |
|---|---|
| `GameServer.Transport.WebSocket` | `rt.AddWebSocket()` |
| `GameServer.Transport.Tcp` | `rt.AddTcp()` |

To drop one, remove its line and its `ProjectReference`. If the config names a transport that no plugin provides, the server
refuses to start and says which one is missing.

- `websocket` listens on `/ws`. For `wss` put TLS in front or enable HTTPS in Kestrel.
- `tcp` uses length-prefixed frames on port 7351, with TLS if `Tcp:CertificatePath` is set.
- `GET /v1/realtime` lists what is enabled.

A new protocol: implement `ITransport` (only framing: receive text frames, call `IConnectionHandler`), then
`rt.Add<MyTransport>("mine").Use("mine")`. Chat, friends and parties do not change.

## Messages

JSON `{ "type", "id", "data" }`. After connecting send `auth` with the JWT within 10 seconds.
The full list is in [Realtime protocol](../reference/realtime-protocol).

## Channels

| Channel | Meaning |
|---|---|
| `room:<name>` | Public room, e.g. `room:lobby` |
| `dm:<userId>` | Direct messages |
| `group:<id>` | Clan chat |
| `party:<id>` | Party chat |

## Storage of messages

| Channel | Stored? |
|---|---|
| Direct, clan | Always (kept `RetentionDays`, default 30) |
| Rooms | Only if `GameServer:Chat:PersistRooms` is `true` (default `false`) |
| Party | Never |

This is why `Chat.HistoryAsync` returns nothing for a lobby room by default.

## Conversations and unread counts

The server remembers how far each player has read each **stored** conversation (direct and clan), so unread counts are the
same on every device:

- `GET /v1/chat/conversations` lists the player's direct chats and the clans they are a member of, newest first. Each item has
  `channel` (`dm:<a>:<b>` or `group:<id>`), `kind` (`direct`/`group`), `peerId` or `groupId`, `title`, `lastMessage`,
  `lastReadId` and `unread` (messages from others after `lastReadId`, at most 100). One call replaces reading every
  friend's history at startup.
- `POST /v1/chat/read` `{ "channel": "dm:<userId>", "messageId": 123 }` moves the read mark forward (never back). Messages a
  player sends count as read for them. Party chat and rooms are not stored and answer `not_stored`.

The marks live in the `chat_reads` table (migration 13).

## Chat limits and moderation

`MaxTextLength` 1000, `MessagesPerWindow` 10 per `WindowSeconds` 10, `DirectMessagesFriendsOnly` false.
Muted players get the error `muted`. Admin: `GET /chat/channels`, `GET /chat/messages?channel=`, `DELETE /chat/messages/{id}`,
`POST /chat/send` (system message), `GET /chat/mutes`, `POST /users/{id}/mute` `{ "minutes": 60, "reason": "spam" }`,
`DELETE /users/{id}/mute`, `GET /online`, `POST /users/{id}/disconnect`.

## Server-pushed events from your module

```csharp
await ctx.Chat.PushAsync(userId, "game.xyz", data);   // the client receives type "game.xyz"
```

On the client: `client.Realtime.On("game.xyz", token => ...)`.

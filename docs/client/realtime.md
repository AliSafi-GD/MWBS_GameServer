# Realtime

`client.Realtime` is one live connection (WebSocket by default, TCP optional) used for chat, notifications, party and friend
events, and the matchmaker.

## Connect

```csharp
await client.Realtime.ConnectAsync();      // needs a signed-in session
await client.Realtime.DisconnectAsync();
```

Set the protocol with `options.RealtimeTransport = "websocket"` (default) or `"tcp"`. It must be enabled on the server
([Chat and realtime](../server/realtime)).

With `AutoReconnect` (default on) the client reconnects and signs in again by itself, and pings every 25 seconds.

## Events

| Event | When |
|---|---|
| `Connected`, `Disconnected(reason)`, `Reconnected` | Connection state. Show a "reconnecting" banner |
| `ChatMessageReceived`, `ChatMessageDeleted` | Chat |
| `NotificationReceived` | A new inbox message |
| `PartyInvited`, `PartyUpdated`, `PartyLeft` | Parties |
| `MatchFound`, `MatchmakerExpired` | Matchmaker |
| `EventReceived` | Any server event |
| `On("type", token => ...)` | A specific event, e.g. `friend.request`, `group.invite`, `goal.completed`, or your own from `ctx.Chat.PushAsync` |

Events are raised on the Unity main thread.

```csharp
client.Realtime.On("friend.request", data => Debug.Log("Friend request: " + data));
client.Realtime.Disconnected += reason => ShowBanner("Reconnecting...");
client.Realtime.Reconnected  += () => HideBanner();
```

## Unsubscribe

Always remove handlers when your object is destroyed. A handler on a destroyed object throws.

```csharp
void OnEnable()  { client.Realtime.ChatMessageReceived += OnChat; }
void OnDisable() { client.Realtime.ChatMessageReceived -= OnChat; }
```

## Custom transports

Implement `IRealtimeTransport` and add it to `options.RealtimeTransports`. The server needs a matching `ITransport` plugin.

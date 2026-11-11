# GameServer Unity SDK

Unity client for the GameServer. Package: `com.gameserver.client` (Unity 2021.3+, C# 9).
Uses `Newtonsoft.Json` and `async/await` with `Task`.

## Install

1. Open Unity: **Window > Package Manager > + > Add package from disk...**
   and pick `sdk/unity/com.gameserver.client/package.json`.
   (Or add `"com.gameserver.client": "file:../path/to/sdk/unity/com.gameserver.client"` to `Packages/manifest.json`.)
2. The package depends on `com.unity.nuget.newtonsoft-json` (3.2.1); Unity installs it automatically.
3. Optional sample: Package Manager > GameServer Client > Samples > QuickStart.

### UDP (LiteNetLib), optional

1. Put `LiteNetLib.dll` (1.3.x) in any `Assets/Plugins` folder.
2. Add the scripting define `GAMESERVER_LITENETLIB` (Project Settings > Player > Scripting Define Symbols).
3. Start the server with `GameServer__Match__Transports=websocket,udp`.
4. In code: `options.UseLiteNetLib();` (namespace `GameServer.Client.Udp`).

Without the define, the UDP assembly is not compiled, so nothing else breaks.

## Quick start

```csharp
using GameServer.Client;

var client = new GameClient(new GameClientOptions {
    BaseUrl = "http://localhost:5000",
    Storage = new PlayerPrefsStorage(),   // keeps the session between runs
});

if (!await client.Auth.TryRestoreSessionAsync())
    await client.Auth.LoginDeviceAsync();

var account = await client.Account.GetAsync();

await client.Realtime.ConnectAsync();
client.Realtime.ChatMessageReceived += m => Debug.Log(m);
await client.Realtime.JoinChannelAsync(Channels.Global);
await client.Realtime.SendMessageAsync(Channels.Global, "hello");
```

Events are raised on the Unity main thread (the `SynchronizationContext` seen when `GameClient` is created,
so create it from the main thread).

> AI agents: read [AGENTS.md](AGENTS.md) first.

## Areas

| Property | What |
|---|---|
| `Auth` | device/provider login, restore, logout |
| `Account`, `Config` | profile, remote config (ETag), version check |
| `Economy`, `Shop` | wallet, inventory, buy |
| `Progress` | stats, achievements, quests, daily reward |
| `Competitions`, `Notifications`, `Rpc` | leaderboards, inbox, custom RPC |
| `Social.Friends/Groups/Parties`, `Chat` | social REST |
| `Realtime` | ws/tcp: chat, notifications, party, matchmaker |
| `Matches` | REST + `ConnectAsync` to a match |

## Matchmaking and matches

```csharp
client.Realtime.MatchFound += async found => {
    var conn = await client.Matches.ConnectAsync(found);
    conn.MessageReceived += (data, delivery) => { /* ... */ };
    await conn.SendJsonAsync(new { x = 1 });
};
await client.Realtime.AddToMatchmakerAsync("duel");
```

## Errors

All failures throw `ApiException` (`Code`, `Message`, `Status`). `IsNetworkError` is true for connectivity problems.
`client.Auth` raises `SessionEnded` when refresh fails and the player must log in again.

## Transports

- Realtime: `options.RealtimeTransport = "websocket"` (default) or `"tcp"`. Add your own via `IRealtimeTransport`.
- Match: websocket by default, UDP via `UseLiteNetLib()`. Add your own via `IMatchTransportClient`.
- HTTP: `UnityWebRequestHttp` is used inside Unity; `HttpClientHttp` elsewhere. Swap via `options.Http`.

## WebGL

Raw TCP and UDP are not available in browsers. Use websocket. The default .NET `ClientWebSocket`
may not work in WebGL builds; a JS-based transport would need to be plugged in via `IRealtimeTransport`.

## Testing

`sdk/unity/SdkSmokeTest` compiles the SDK sources as plain .NET and runs them against a live server.
`run-tests.ps1` / `run-tests.sh` build and run it after the server smoke test; results land in
`logs/sdk-test.log` and in `logs/REPORT.txt`.

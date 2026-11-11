# AGENTS.md: building a Unity game with the GameServer SDK

This file is for an AI coding agent (or a developer in a hurry). It explains how to build a complete online game in Unity
on top of the GameServer backend using the package `sdk/unity/com.gameserver.client`.
Read it fully before writing code. Details of each type are in the source (`Runtime/**`); this file tells you which calls
to use and which mistakes to avoid.

## 0. Ground rules

1. **The server is the authority.** Never let the client decide coins, XP, wins, scores or inventory. The client sends
   *intent* (input, "buy product X", "claim quest Y"); the server validates and answers.
2. **All network calls are `async Task`.** Use `await`. Never block with `.Result` or `.Wait()` (it deadlocks the Unity main thread).
3. **Create `GameClient` once, on the main thread** (for example in a bootstrap MonoBehaviour marked `DontDestroyOnLoad`).
   SDK events are raised on the Unity main thread only because of that, so they may touch GameObjects directly.
4. **Every failure throws `ApiException`** (`Code`, `Message`, `Status`, `IsNetworkError`). Wrap UI-triggered calls in try/catch and
   show `Code`-based messages. Do not parse `Message`.
5. Currency and item names, shop products, quests, achievements, leaderboards, tournaments and match modes are
   **server config** (admin panel, Config tab), not code. Read them from the server; do not hard-code ids you did not define.
6. Use `requestId` on purchases (`Shop.BuyAsync(id, qty, requestId)`) with a fresh GUID per click, so a retry cannot double-charge.
7. Always unsubscribe from events in `OnDestroy`.

## 1. Setup

1. Package Manager > Add package from disk > `sdk/unity/com.gameserver.client/package.json` (Unity 2021.3+). Newtonsoft.Json installs itself.
2. Server URL: `http://localhost:5000` locally. Use `https` in production.
3. Optional UDP for fast action games: put `LiteNetLib.dll` (1.3.x) in `Assets/Plugins`, add define `GAMESERVER_LITENETLIB`, call `options.UseLiteNetLib()`.
4. WebGL: only websocket works (no TCP/UDP).

## 2. Bootstrap (copy this)

```csharp
using System;
using System.Threading.Tasks;
using GameServer.Client;
using UnityEngine;

public class Backend : MonoBehaviour
{
    public static Backend I { get; private set; }
    public GameClient Client { get; private set; }
    public string serverUrl = "http://localhost:5000";

    async void Awake()
    {
        if (I != null) { Destroy(gameObject); return; }
        I = this; DontDestroyOnLoad(gameObject);

        Client = new GameClient(new GameClientOptions {
            BaseUrl = serverUrl,
            Storage = new PlayerPrefsStorage(),   // keeps the session between runs
            Log = Debug.Log,
        });
        Client.Auth.SessionEnded += () => { /* refresh failed: show login screen */ };

        try {
            if (!await Client.Auth.TryRestoreSessionAsync())
                await Client.Auth.LoginDeviceAsync();      // guest account bound to this device
            await Client.Realtime.ConnectAsync();          // chat, matchmaking, notifications, parties
        } catch (ApiException e) {
            Debug.LogError($"{e.Code}: {e.Message} (network: {e.IsNetworkError})");
        }
    }

    async void OnDestroy() { if (I == this && Client != null) await Client.Realtime.DisconnectAsync(); }
}
```

`Client.Session.UserId` is the signed-in player id. `Client.Realtime` raises `Disconnected` and `Reconnected`; show a "reconnecting" banner.

## 3. API cheat-sheet

All methods take an optional `CancellationToken` last. Names are exact.

### Auth and account
| Call | Notes |
|---|---|
| `Auth.LoginDeviceAsync(username=null, create=true, deviceId=null)` | guest login. Returns `LoginResult` |
| `Auth.ProvidersAsync()` | enabled login providers |
| `Auth.LoginAsync(provider, credential, username=null, create=true)` | e.g. email/password, social. `credential` is an object whose shape depends on the provider |
| `Auth.BeginAsync(provider, credential)` | first step for providers that send a code (then `LoginAsync` with the code) |
| `Auth.TryRestoreSessionAsync()` | true if a saved session is still valid |
| `Auth.LogoutAsync()`, `Auth.LogoutEverywhereAsync()` | |
| `Account.GetAsync()`, `Account.UpdateAsync(new ProfileUpdate{...})`, `Account.DeleteAsync()` | profile |
| `Account.LinkAsync(provider, credential)`, `Account.UnlinkAsync(provider)` | attach email/social to a guest account so progress is not lost |
| `Account.GetUsersAsync(ids)` | public profiles of other players |

### Config
`Config.GetAsync()` returns `Dictionary<string, JToken>` (remote config, ETag cached). `Config.VersionCheckAsync(version, store)` returns
`VersionCheck` (force-update / soft-update). Call it at startup.

### Economy and shop
| Call | Returns |
|---|---|
| `Economy.WalletAsync()` | `Dictionary<string,long>` currency -> amount |
| `Economy.InventoryAsync()` | `Dictionary<string,long>` item -> count |
| `Shop.ListAsync(category=null)` | `List<ShopProduct>` (price, limits, availability) |
| `Shop.BuyAsync(productId, quantity=1, requestId=null)` | `ShopPurchase`. Errors: `insufficient_funds`, `product_unavailable`, `limit_reached`, `product_not_found` |

### Progress (stats, quests, achievements, daily reward)
| Call | Notes |
|---|---|
| `Progress.StatsAsync()` | counters like `matches_won` (written by the server) |
| `Progress.AchievementsAsync()` / `ClaimAchievementAsync(id)` | `Goal` has progress/target/claimed. Claim only when complete (`goal_not_complete`, `already_claimed`) |
| `Progress.QuestsAsync()` / `ClaimQuestAsync(id)` | daily/weekly quests, same pattern |
| `Progress.DailyAsync()` / `ClaimDailyAsync()` | login streak reward (`daily_already_claimed`) |

### Leaderboards and tournaments (`Competitions`)
`LeaderboardAsync(id, limit=50, around=0, period=null)` returns `Board` (entries + your rank; `around` centers on you).
`TournamentsAsync()`, `TournamentAsync(id, limit, around)`, `JoinTournamentAsync(id)`, `ClaimTournamentAsync(id)`.
**Scores are submitted by the server** (match handler or module), never by the client.

### Notifications and RPC
`Notifications.ListAsync(limit, unreadOnly)`, `MarkReadAsync(id)`, `ClaimAsync(id)` (notification with a reward), `DeleteAsync(id)`.
Live: `Realtime.NotificationReceived`. `Rpc.CallAsync(id, payload)` (string) or `Rpc.CallAsync<T>(id, request)` runs custom server logic
(use it for anything game-specific that is not a match, e.g. "open a loot box").

### Social
- `Social.Friends`: `ListAsync(state)` (`friend`, `invite_received`, `invite_sent`, `blocked`), `AddAsync(userId|username)`, `RemoveAsync`, `BlockAsync`, `UnblockAsync`, `OnlineAsync(userIds)`.
- `Social.Groups` (clans): `CreateAsync(NewGroup)`, `SearchAsync(query,type)`, `MineAsync()`, `GetAsync`, `UpdateAsync`, `DeleteAsync`, `JoinAsync`, `LeaveAsync`, `MembersAsync`, `InviteAsync`, `AcceptAsync`, `KickAsync`, `BanAsync`, `UnbanAsync`, `PromoteAsync`, `DemoteAsync`, `TransferAsync`.
- `Social.Parties` (temporary squads): `CreateAsync(open,maxSize)`, `MineAsync`, `GetAsync`, `JoinAsync`, `LeaveAsync`, `InviteAsync`, `KickAsync`, `PromoteAsync`, `CloseAsync`. Events: `Realtime.PartyInvited`, `PartyUpdated`, `PartyLeft`.
- `Chat.HistoryAsync(channel, limit, before)`.

### Realtime (needs `Realtime.ConnectAsync()` first)
Chat channels: `Channels.Room("lobby")`, `Channels.Group(id)`, `Channels.Party(id)`, `Channels.Direct(userId)`.
```csharp
var ch = await rt.JoinChannelAsync(Channels.Room("lobby"));
await rt.SendMessageAsync(ch, "hi");
rt.ChatMessageReceived += m => ...;   // ChatMessage: SenderName, Text, ...
rt.ChatMessageDeleted += (channel, id) => ...;
```
Other events: `Connected`, `Disconnected(reason)`, `Reconnected`, `MatchFound`, `MatchmakerExpired`, `NotificationReceived`, `EventReceived`,
and `rt.On("custom.type", token => ...)` for custom server events (for example `friend.request`).

## 4. Matches (the part that makes it a game)

A match is one running game session. Its rules run **on the server** in a `MatchHandler` (C#, in a server module). The client connects and
exchanges raw bytes with it. Message format is whatever your client and handler agree on (JSON or binary).

### Client flow
```csharp
var modes = await client.Matches.ModesAsync();                 // what the server offers (names, min/max players, transport)

client.Realtime.MatchFound += async found => {
    var conn = await client.Matches.ConnectAsync(found);       // picks websocket or udp from the mode
    conn.DataReceived += data => HandleServerMessage(data);    // raw bytes from the handler
    conn.Closed += reason => BackToLobby(reason);
    await conn.SendJsonAsync(new { t = "hello" });             // also SendAsync(byte[], delivery) / SendTextAsync
};
var ticket = await client.Realtime.AddToMatchmakerAsync("duel", new Dictionary<string,object>{ {"region","eu"} });
// cancel: await client.Realtime.RemoveFromMatchmakerAsync(ticket.Id);
```
- Connect within ~30 s of `MatchFound` or the seat is lost.
- **Reconnect**: if the connection drops mid-match, call `client.Matches.JoinAsync(matchId)` then `ConnectAsync(info)`; you have ~15 s in the sample game.
- Parties: leader calls `AddToMatchmakerAsync(mode, props, partyId)` to queue the whole party.
- Private/custom rooms: `Matches.CreateAsync(mode)` (only modes with `allowClientCreate`), `ListAsync(mode)`, `JoinAsync(matchId)`.
- Delivery: `MatchDelivery.Reliable` (default), `Unreliable`, `Sequenced` (UDP only; websocket treats all as reliable). Use `Unreliable` for per-tick input/positions.
- The built-in `relay` handler forwards each message to the other players with a leading slot byte: parse with `RelayMessage.TryParse(data, out slot, out payload)`. Use it only for turn-based/casual games; for anything competitive write a real handler.

### Server side: writing the game rules
Add a class to a module project (see `samples/HelloGame.Modules/Arena/ArenaMatch.cs`, a complete working example):
```csharp
public sealed class MyModule : IServerModule {
    public void Initialize(IModuleInitializer init) => init.RegisterMatchHandler("mygame", () => new MyMatch());
}
public sealed class MyMatch : MatchHandler {
    public override Task OnJoinAsync(IMatch m, MatchPlayer p) { ... }
    public override Task OnMessageAsync(IMatch m, MatchPlayer p, ReadOnlyMemory<byte> data) { ... }  // validate input here
    public override Task OnTickAsync(IMatch m) { ... }      // simulation step (tickRate per second)
    public override Task OnLeaveAsync(IMatch m, MatchPlayer p, MatchLeaveReason r) { ... }
}
```
Inside a handler use `m.BroadcastAsync / SendAsync / KickAsync / End()`, per-match data in `m.State`, and pay out through `m.Services`:
`Economy.ApplyAsync`, `Progress.AddStatAsync(userId,"matches_won")` / `SetStatMaxAsync`, `Leaderboards.SubmitAsync(id,userId,score)`, `Notifications`, `Config`.
Handler calls are serialized per match: no locks needed.

Then define the mode in the **`matches` config** (admin panel > Config):
```json
{ "mygame": { "handler": "mygame", "transport": "websocket", "minPlayers": 2, "maxPlayers": 4, "tickRate": 20 } }
```
Optional: `fillSeconds`, `ticketTimeoutSeconds`, `joinTimeoutSeconds`, `emptyTimeoutSeconds`, `matchmaking`, `allowClientCreate`.

## 5. Where the game content is defined (server config keys)

`shop`, `achievements`, `quests`, `daily_rewards`, `leaderboards`, `tournaments`, `matches`, `social`. Edit them in the admin panel
(`/admin`, Config tab; each save is versioned and can be rolled back). Stats feed achievements and quests: a quest with `"stat": "matches_won"`
advances when a handler calls `AddStatAsync(user, "matches_won")`. Example config files: `samples/HelloGame.Modules` and `src/GameServer.Host/wwwroot/arena/arena-config.json`.

## 6. Error codes you will meet

`insufficient_funds`, `insufficient_items`, `product_not_found`, `product_unavailable`, `limit_reached`, `goal_not_complete`, `already_claimed`,
`daily_already_claimed`, `already_queued`, `matchmaking_disabled`, `match_not_found`, `match_full`, `match_create_forbidden`, `kicked_from_match`,
`not_friends`, `friend_limit`, `already_in_party`, `party_full`, `not_party_leader`, `not_group_admin`, `group_full`, `banned_from_group`,
`invalid_credential`, `invalid_refresh_token`, `identity_in_use`, `account_disabled`, `app_version` (client too old), HTTP 429 (rate limit: back off and retry).

## 7. Recommended game architecture

1. `Backend` singleton (section 2). One scene "Boot": login, version check, load config/wallet, then load "Menu".
2. Thin service classes per area (`WalletService`, `ShopService`, `MatchService`) that call the SDK and expose C# events to the UI. UI never calls the SDK directly.
3. Game scene: `MatchService` owns the `MatchConnection`; decode server snapshots into a state object; render from it (interpolate between snapshots); send only input.
4. Show loading/error states for every awaited call; disable buttons while a call is in flight (double taps cause duplicate requests).
5. Save nothing authoritative locally. The only thing stored on the device is the session (`PlayerPrefsStorage`).

## 8. Reference implementations in this repo

- `sdk/unity/com.gameserver.client/Samples~/QuickStart/QuickStart.cs`: login, wallet, chat, matchmaking, match connection.
- `samples/HelloGame.Modules/Arena/ArenaMatch.cs` + `src/GameServer.Host/wwwroot/arena/`: server-authoritative 1v1 tank game (message protocol documented at the top of the file), with bots in `tests/`.
- `sdk/unity/SdkSmokeTest/`: every SDK call exercised against a live server (a good source of correct usage).

## 9. Before you say "done"

- Compile with no warnings in Unity. Test with the server running (`run-tests.ps1` or `docker compose up` + `dotnet run`).
- Test two clients at once (two editor instances or a build + editor) for any multiplayer feature.
- Test: kill the server mid-match, turn off the network, and relaunch the app (session restore). Each must end in a clear UI state, not a hang.

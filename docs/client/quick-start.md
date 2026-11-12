# Quick start

## Ground rules

1. **The server is the authority.** Never let the client decide coins, XP, wins, scores or inventory. Send *intent*.
2. **All network calls are `async Task`.** Use `await`. Never block with `.Result` or `.Wait()`: it deadlocks the Unity main thread.
3. **Create `GameClient` once, on the main thread** (a bootstrap MonoBehaviour with `DontDestroyOnLoad`). SDK events are raised on
   the main thread only because of that, so they may touch GameObjects directly.
4. **Every failure throws `ApiException`** (`Code`, `Message`, `Status`, `IsNetworkError`). Wrap UI-triggered calls in try/catch
   and show messages based on `Code`. Never parse `Message`.
5. Currency and item names, shop products, quests, leaderboards, tournaments and match modes are **server config**. Read them from
   the server; do not hard-code ids you did not define.
6. Use `requestId` on purchases with a fresh GUID per click, so a retry cannot double-charge.
7. Always unsubscribe from events in `OnDestroy`.

## The bootstrap singleton

Copy this as your starting point:

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

`Client.Session.UserId` is the signed-in player id. `Client.Realtime` raises `Disconnected` and `Reconnected`: show a
"reconnecting" banner.

## Areas of the client

| Property | What |
|---|---|
| `Auth` | Device/provider login, restore, logout |
| `Account`, `Config` | Profile, remote config (ETag), version check |
| `Economy`, `Shop` | Wallet, inventory, buy |
| `Progress` | Stats, achievements, quests, daily reward |
| `Competitions`, `Notifications`, `Rpc` | Leaderboards, inbox, custom server functions |
| `Social.Friends / Groups / Parties`, `Chat` | Social REST |
| `Realtime` | ws/tcp connection: chat, notifications, party, matchmaker |
| `Matches` | REST plus `ConnectAsync` to a match |

All methods take an optional `CancellationToken` last.

## The sample script

Package Manager > GameServer Client > Samples > QuickStart contains one script that signs in, reads the wallet, joins
`room:lobby`, queues for a match and plays it. Replace the mode name `"duel"` with one from your server's `matches` config.

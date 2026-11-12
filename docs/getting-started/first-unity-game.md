# Your first Unity game

You have two ways to start. Pick one.

## A. Open the finished sample

`samples/unity-client` is the **Tank Arena** project (Unity 6000.6.0f1). It already references the SDK.

1. Make sure the server runs and you pressed **Write game config** on <http://localhost:5000/arena/> once.
2. Open `samples/unity-client` in Unity Hub.
3. Open the **Boot** scene and press Play. The gear icon (bottom right) sets the server address.

Read [Tank Arena](../guides/tank-arena) to see how the pieces map to the SDK.

## B. Add the SDK to your own project

1. **Window > Package Manager > + > Add package from disk...** and pick
   `sdk/unity/com.gameserver.client/package.json`.
2. Create a script and put it on a GameObject:

```csharp
using GameServer.Client;
using UnityEngine;

public class Hello : MonoBehaviour
{
    async void Start()
    {
        var client = new GameClient(new GameClientOptions {
            BaseUrl = "http://localhost:5000",
            Storage = new PlayerPrefsStorage(),   // keeps the session between runs
            Log = Debug.Log,
        });

        if (!await client.Auth.TryRestoreSessionAsync())
            await client.Auth.LoginDeviceAsync();

        var account = await client.Account.GetAsync();
        Debug.Log("Signed in as " + account.Username);

        var wallet = await client.Economy.WalletAsync();
        foreach (var coin in wallet) Debug.Log(coin.Key + ": " + coin.Value);
    }
}
```

3. Press Play. The console shows your account and wallet.

::: tip Android devices and the editor
`localhost` on a phone is the phone. Use your computer's LAN address, start the server on `0.0.0.0`
and open the firewall port. See [Build an Android APK](../guides/android).
:::

## Next

Read the [Unity client guide](../client/installation), starting with [Quick start](../client/quick-start).

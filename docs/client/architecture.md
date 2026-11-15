# Architecture tips

A layout that works well, taken from the Tank Arena client.

1. **A `Backend` singleton** ([Quick start](./quick-start)). One "Boot" scene: version check, config, login, wallet, then load "Menu".
2. **Thin services per area** (`WalletService`, `ShopService`, `ChatService`, `PlayService`) that call the SDK and expose C#
   events. **The UI never calls the SDK directly.**
3. **Game scene**: a service owns the `MatchConnection`; decode server snapshots into a state object; render from it
   (interpolate between snapshots); send only input.
4. **Loading and error states** for every awaited call. Disable buttons while a call is in flight.
5. **Save nothing authoritative locally.** The only thing stored on the device is the session (`PlayerPrefsStorage`).
   Things like "which chat messages were read" are fine to keep in PlayerPrefs.

## Test game code without a server

Every SDK API has an interface: `IGameClient` for the whole client, and `IShopApi`, `IProgressApi`, `IRealtimeClient`, ... for
each area (`client.Shop` is an `IShopApi`). Let your services take the interface they need, and a test can hand them a fake:

```csharp
public sealed class ShopService
{
    private readonly IShopApi _shop;
    public ShopService(IShopApi shop) => _shop = shop;   // new ShopService(Backend.I.Client.Shop) in the game
}

sealed class FakeShop : IShopApi
{
    public List<ShopProduct> Products = new List<ShopProduct>();
    public Task<List<ShopProduct>> ListAsync(string category = null, CancellationToken ct = default) => Task.FromResult(Products);
    public Task<ShopPurchase> BuyAsync(string productId, int quantity = 1, string requestId = null, CancellationToken ct = default) =>
        Task.FromResult(new ShopPurchase { ProductId = productId, Quantity = quantity });
}
```

To test against the real SDK with a fake server instead, give `GameClientOptions` your own `Http` (an `IHttp` that answers from a
table), `Storage = new MemoryStorage()` and `Dispatcher = new InlineDispatcher()` (events run at once, no main loop needed). The
package's own tests in `Packages/com.gameserver.client/Tests/Editor` do exactly that; add `"testables": ["com.gameserver.client"]`
to `Packages/manifest.json` to see them in the Test Runner.

## Tank Arena folders

```
Assets/Scripts/
  Core/    services and session, no UI            (Core/Services/*.cs)
  Meta/    boot, menu pages, popups, UI kit       (Boot, Menu, Ui)
  Arena/   the match: screen, state, touch sticks
  Editor/  scene and Android build scripts
  Testing/ headless bot
Assets/Tests/PlayMode/GameTours.cs   PlayMode tests that tour the whole UI and take screenshots
```

## Before you say "done"

- Compile with no warnings in Unity.
- Test with the server running (`run-tests.ps1`, or Docker plus `dotnet run`).
- Test two clients at once for any multiplayer feature.
- Kill the server mid-match, turn off the network, relaunch the app (session restore). Each must end in a clear UI state.

## For AI coding agents

`sdk/unity/AGENTS.md` is a compact version of the SDK guide written for coding agents: ground rules, bootstrap, cheat-sheet,
match flow, error codes. Point your agent at it before it writes code.

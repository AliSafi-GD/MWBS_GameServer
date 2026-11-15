# Changelog

All notable changes of `com.gameserver.client`. The version follows the package's own API, not the server's.

## [0.2.0] - 2026-11-15

### Added
- Interfaces for every API: `IGameClient`, `IAuthApi`, `IAccountApi`, `IConfigApi`, `IEconomyApi`, `IShopApi`, `IProgressApi`,
  `ICompetitionsApi`, `INotificationsApi`, `IRpcApi`, `ISocialApi` (`IFriendsApi`, `IGroupsApi`, `IPartiesApi`), `IChatApi`,
  `IMatchesApi`, `IRealtimeClient`. Write game code against them and hand it a fake in tests.
- `IDispatcher` and `GameClientOptions.Dispatcher`: choose where SDK events are raised. `SynchronizationContextDispatcher` is the
  default (the thread that creates the client, Unity's main thread); `InlineDispatcher` runs them at once (tests, bots).
- Package tests in `Tests/Editor` (Unity Test Runner, EditMode). The same files run without Unity:
  `dotnet test sdk/unity/SdkUnitTests`.

### Changed
- `GameClient` implements `IGameClient`, and its properties are interface types (`client.Shop` is an `IShopApi`, ...);
  `SocialApi.Friends`, `.Groups`, `.Parties` likewise. Calls stay the same. Code that stored them in a variable of the class type
  (`ShopApi shop = client.Shop;`) has to use the interface (`IShopApi`) or `var`.
- Source files split by area (no file over 300 lines): models (`Models/*Models.cs`), the realtime client (partial class in three
  files) and the realtime transports (one file each). No type was renamed or moved to another namespace.

## [0.1.0]

- First version: accounts, wallet, shop, progress, leaderboards, notifications, social, chat, matchmaking, matches over
  WebSocket/TCP/UDP.

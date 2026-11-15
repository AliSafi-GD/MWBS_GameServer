# Server architecture

How the server code is split, how the parts are wired together, and where to change what. Read this before you change the
server itself (not only write a module).

## Projects

```
GameServer.Abstractions            the contract for modules (IServerModule, IEconomy, MatchHandler, ...). Only ever grows.
GameServer.Core                    the game services and the store interfaces they use. No database code.
GameServer.Infrastructure.Postgres PostgreSQL implementation of every store + the schema migrations.
GameServer.Transport.*             WebSocket, TCP and UDP (LiteNetLib), each a plugin.
GameServer.Host                    the executable: Program.cs, one Endpoints/ file per API area, Middleware/, admin panel.
```

The arrows only point inwards: Host → Infrastructure.Postgres → Core → Abstractions. Core does not know which database is
used; a test can run every Core service with in-memory stores.

## Wiring (Program.cs)

```csharp
builder.Services.AddGameServerCore(builder.Configuration, builder.Environment.IsDevelopment(),
    realtime: rt => rt.AddWebSocket().AddTcp(),
    match: mt => mt.AddWebSocket().AddLiteNetLib());

// Where the data lives (ConnectionStrings:Default).
builder.Services.AddGameServerPostgres(builder.Configuration);
```

- `AddGameServerCore` registers the services (shop, progress, chat, matches, ...). It needs no connection string.
- `AddGameServerPostgres` registers the data source, `DatabaseMigrator` (run once at startup) and a Postgres class for every
  store interface. It stops with a clear message when `ConnectionStrings__Default` is missing.

Another database would be another `AddGameServer...` call that registers its own classes for the same interfaces.

## Stores

A service holds the rules, a store only keeps the data. Each consumer asks for the smallest interface it needs.

| Interface | What it stores | Used by |
|---|---|---|
| `IAccountStore` | accounts: read, profile, delete | most services, account endpoints |
| `IIdentityStore` | device logins, linked identities (Google Play, phone) | login and link endpoints |
| `IUserAdminStore` | search, ban, export | admin endpoints |
| `IShopPurchaseStore` | shop limit counters | `ShopService` |
| `IDailyRewardStore` | login streaks (a claim locks the row until commit) | `DailyRewardService` |
| `IProgressStore` | stats per period, taken goal rewards | `ProgressService` |
| `IEconomy` / `IEconomyStore` | wallet, inventory, ledger | shop, rewards, modules |
| `IConfigStore` | config keys | everything config-driven |
| `IFriendStore`, `IGroupStore`, `IChatStore` | social data, chat history | social and chat services |
| `IAdminAccountStore`, `IAdminAuditLog`, `IConfigHistoryStore`, `IPlayerAdminData`, `IBroadcastStore`, `IAdminMetrics` | admin panel data | admin endpoints, `AdminBackground` |

Pure rules that do not need the database live next to the services and are tested on their own, for example
`DailyStreak` (daily reward dates and streaks), `PeriodKeys` (day/week/month keys), `AdminKeys` and `AdminRules`.

## Talking to online players

Apart from the realtime hub (which owns the connections) and the dashboard sampler, services do not depend on the connection
registry (`ConnectionManager`) itself, only on what they use:

- `IPlayerPush`: send an event to a player or to everyone (goal reached, new notification).
- `IRealtime`: who is online, channels, disconnecting a player (social services, matchmaker).
- `IChannelSubscriptions`: joining and leaving chat channels (chat).
- `IPartyMembership`, `IGroupCleanup`: what chat, the matchmaker and account deletion need from parties and clans.

## Time

No server code (`src/`) reads `DateTime.UtcNow`. Every service gets the clock as `TimeProvider` (`_time.UtcNow()`).
`AddGameServerCore` registers `TimeProvider.System` with `TryAdd`, so a test or a host can register its own clock first,
for example a `FakeTimeProvider` that moves a day forward in one line.

## Two cycles, on purpose

Two dependencies go in a circle. They are injected as `Lazy<T>`, so the circle is visible in the constructor and the
service is created on first use:

- `HookRunner` → `Lazy<HookServices>`: the services a hook gets include chat and social, which run hooks themselves.
- `MatchService` → `Lazy<IEnumerable<IMatchTransport>>`: the transports take `MatchService` as their `IMatchHost`.

No service takes `IServiceProvider`.

## NuGet versions

Every package version of the repository is in `Directory.Packages.props` at the root, exact (`10.0.3`, not `10.*`), so every
build gets the same packages. Projects list packages without a version. To update one, change its number there and run
`run-tests.ps1`. The Dockerfile copies this file next to `Directory.Build.props`; `LiteNetLib` must stay the same version as
the `LiteNetLib.dll` Unity clients use.

## Testing a service

`tests/GameServer.Core.Tests` builds the real services with `AddGameServerCore`, adds in-memory stores
(`Fakes.cs`) and a `FakeTimeProvider`, and calls them like an endpoint would:

```csharp
using var s = new TestServer(new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc));
s.Config.Set(ConfigRules.Shop, """{ "products": { "pack": { ... "limit": { "per": "day", "count": 2 } } } }""");
s.Economy.Give("u1", "gold", 1000);

await s.Get<ShopService>().BuyAsync("u1", new ShopBuyRequest("pack"), default);
s.Clock.Advance(TimeSpan.FromHours(1)); // next day: the limit starts again
```

`WiringTests` builds the full Core + Postgres container with `ValidateOnBuild`, so a missing registration or a new cycle fails
a unit test instead of the server start. The Postgres classes themselves are covered by the smoke test against a real database
(see [Testing](/guides/testing)).

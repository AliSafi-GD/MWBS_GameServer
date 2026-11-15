# Writing a module

A module is where **your game's server logic** lives: RPC functions, hooks and match handlers. It is a normal .NET class library.
The host loads `modules/<Name>/<Name>.dll` at start.

## 1. Create the project

```bash
dotnet new classlib -n MyGame.Modules -o MyGame.Modules
```

Reference `GameServer.Abstractions` the way the sample does. `Private=false` and `ExcludeAssets=runtime` mean "the host's copy is
the only one" (do not ship a second copy):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\GameServer.Abstractions\GameServer.Abstractions.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

Easiest start: copy `samples/HelloGame.Modules` and rename it.

::: tip NuGet packages in a module inside this repository
The repository manages package versions centrally in `Directory.Packages.props`. A project anywhere below the repository root
writes `<PackageReference Include="Some.Package" />` **without** a `Version` and adds
`<PackageVersion Include="Some.Package" Version="1.2.3" />` to `Directory.Packages.props`; a `Version` on the reference
fails with NU1008. A module in its own repository is not affected.
:::

## 2. Implement `IServerModule`

The host finds every non-abstract type that implements `IServerModule` in the DLL, creates it with a parameterless constructor
and calls `Initialize` once.

```csharp
using System.Text.Json;
using GameServer.Abstractions;

namespace MyGame.Modules;

public sealed class MyModule : IServerModule
{
    public void Initialize(IModuleInitializer init)
    {
        init.RegisterRpc("hello", (ctx, payload) =>
            Task.FromResult(JsonSerializer.Serialize(new { message = $"hello {ctx.UserId}" })));

        init.RegisterMatchHandler("mygame", () => new MyMatch());

        init.RegisterAfter(HookOps.AuthLogin, async (ctx, request, response) =>
        {
            var tx = new EconomyTransaction("welcome_gift", "welcome").Currency("coins", 50);
            await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);
        });
    }
}
```

`IModuleInitializer` offers five things: `RegisterRpc`, `RegisterBefore`, `RegisterAfter`, `RegisterMatchHandler` and
`RegisterIdentityProvider` (your own login method, see [Authentication](./authentication#writing-your-own-provider)).

## 3. Build into `modules/`

The folder name must equal the DLL name:

```bash
dotnet publish MyGame.Modules -c Release -o modules/MyGame.Modules
```

Restart the host. `GET /healthz` and the admin **Modules** tab list what was registered.

## 4. Ship the game config with the module

Put the game's config (shop, quests, match modes, leaderboards...) in `config/<key>.json` next to the project file and add one
item to the `.csproj` so `dotnet publish` copies it into `modules/<Name>/config/`:

```xml
<ItemGroup>
  <None Include="config\*.json" CopyToPublishDirectory="PreserveNewest" LinkBase="config" />
</ItemGroup>
```

The server checks the files and writes them into the database at startup. Format, modes (`seed`, `sync`, `off`) and the export
of an existing server are in [Game config files](./game-config). The sample module's `config/` folder is a complete example.

## What a module can use

`RpcContext` (and `HookContext`) gives you:

| Property | Use |
|---|---|
| `UserId` | The player who made the call |
| `Storage` | Per-user JSON documents: `ReadAsync<T>(userId, key)`, `WriteAsync<T>(userId, key, value)` |
| `Config` | Read any remote config key: `GetAsync<T>("key")` |
| `Economy` | Atomic wallet and inventory transactions |
| `Leaderboards` | Submit scores |
| `Notifications` | Send inbox messages with rewards |
| `Social` | Friends, groups, parties from server code |
| `Chat` | `PushAsync(userId, type, data)` custom realtime events |
| `Matches` | Create and end matches |
| `Progress` | Report stats |
| `CancellationToken` | Pass it to every async call |

::: tip Versioning
`GameServer.Abstractions` is the contract between host and module. It is only ever extended, so a module built against an
older version keeps working.
:::

## Errors

Throw `GameException(code, message)`. The client receives `{ "error": code, "message": message }` and the SDK raises
`ApiException` with the same `Code`.

```csharp
throw new GameException("invalid_score", "Score must be between 0 and 1000000.");
```

Next: [RPC functions](./rpc), [Hooks](./hooks), [Match handlers](./match-handlers).

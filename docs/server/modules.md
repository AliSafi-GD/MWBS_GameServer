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

`IModuleInitializer` offers four things: `RegisterRpc`, `RegisterBefore`, `RegisterAfter`, `RegisterMatchHandler`.

## 3. Build into `modules/`

The folder name must equal the DLL name:

```bash
dotnet publish MyGame.Modules -c Release -o modules/MyGame.Modules
```

Restart the host. `GET /healthz` and the admin **Modules** tab list what was registered.

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

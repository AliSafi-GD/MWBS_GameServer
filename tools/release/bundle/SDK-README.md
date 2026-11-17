# Writing a game module (offline)

This folder has everything to build a module without internet access (the .NET 10 SDK must be installed).

- `packages/GameServer.Abstractions.__VERSION__.nupkg`: the contract a module compiles against
- `lib/net10.0/GameServer.Abstractions.dll` (+ `.xml`): the same as a plain DLL, if you prefer a direct reference
- `templates/module/`: a `dotnet new` template for a new module

## Once per machine

```bash
dotnet nuget add source "<this folder>/packages" --name gameserver
dotnet new install "<this folder>/templates/module"
```

## A new module

```bash
dotnet new gameserver-module -n MyGame
cd MyGame
dotnet publish -c Release -o <server>/modules/MyGame.Modules
```

Restart the server; it loads the module and its `config/*.json` files.

Compatibility: a module built against this version runs on this and every later server version.

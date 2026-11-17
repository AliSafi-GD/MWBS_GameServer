# GameServer.Abstractions

The contract between the GameServer and a game module. A module is a .NET class library that implements `IServerModule`
and registers RPC functions, hooks, match handlers and login methods. The server loads it from `modules/<Name>/<Name>.dll`.

Reference it so the module does **not** ship its own copy (the server provides it at run time):

```xml
<PackageReference Include="GameServer.Abstractions" Version="0.1.0" ExcludeAssets="runtime" />
```

The quickest start is the module template that comes with the server release:

```bash
dotnet new install <release>/sdk/templates/module
dotnet new gameserver-module -n MyGame
```

Compatibility: this package only ever grows. A module built against an older version runs on a newer server.

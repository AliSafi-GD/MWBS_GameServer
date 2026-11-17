# MyGame.Modules

The server side of the game, as a GameServer module.

```bash
dotnet publish -c Release -o <server>/modules/MyGame.Modules
```

Then restart the server. It loads `modules/MyGame.Modules/MyGame.Modules.dll` and the files in its `config/` folder.

- RPC functions, hooks and match handlers: `MyGameModule.cs`
- Game config (shop, quests, match modes...): `config/*.json`

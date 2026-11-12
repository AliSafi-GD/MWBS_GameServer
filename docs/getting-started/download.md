# Download the project

The server, the Unity SDK, the sample game and these docs live in one repository.

```bash
git clone https://github.com/AliSafi-GD/MWBS_GameServer.git
cd MWBS_GameServer
```

You can also download a ZIP from the repository page (**Code > Download ZIP**), but cloning makes it easy to pull updates.

## What is in the folder

| Path | Purpose |
|---|---|
| `src/` | The server (Abstractions, Core, Host and the transport plugins) |
| `samples/HelloGame.Modules/` | Example server module you can copy to start your own |
| `sdk/unity/com.gameserver.client/` | The Unity package |
| `sdk/unity/AGENTS.md` | A compact guide to the SDK, written for AI coding agents and developers in a hurry |
| `samples/unity-client/` | The Tank Arena Unity project |
| `postman/` | Postman collection and `.http` files (Rider / VS Code) with ready requests |
| `tests/` | `GameServer.SmokeTest` and `GameServer.ArenaBots` |
| `docker-compose.yml`, `Dockerfile` | Run everything in containers |
| `.env.example` | Settings you copy to `.env` |
| `docs/` | This documentation |

::: warning The repository is public
`appsettings.Development.json` and the Postman files contain **development-only** keys. Never use them in production.
See [Deploy to production](../server/production).
:::

## Next

[Run the server](./run-the-server)

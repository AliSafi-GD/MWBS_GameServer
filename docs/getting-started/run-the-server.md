# Run the server (for development)

This page runs the server **on your own machine while you build your game**: `dotnet run`, development keys, http on port 5000.
Do not use it for a server that players connect to. For that, see [Install the server](../install/) (Docker Compose, Linux,
Windows, or a VPS).

Run these commands from the repository root. Three steps: database, module, host.

## 1. Start PostgreSQL

```bash
cp .env.example .env
docker compose up -d postgres
```

For local development leave `DB_PASSWORD=change-me` in `.env`: it matches `appsettings.Development.json`.

::: tip Port 5432 already used?
If a local PostgreSQL service already owns port 5432, run the container on another port and tell the host:

```bash
docker run -d --name mwbs-pg -e POSTGRES_DB=game -e POSTGRES_USER=game -e POSTGRES_PASSWORD=change-me \
  -p 127.0.0.1:5433:5432 -v mwbs_pgdata:/var/lib/postgresql/data postgres:17
```

Then start the host with
`ConnectionStrings__Default="Host=localhost;Port=5433;Database=game;Username=game;Password=change-me"`.
:::

## 2. Build the sample module

A module is a DLL in `modules/<Name>/<Name>.dll`. The folder name must equal the DLL name.

```bash
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules
```

`modules/` is not in git, so do this once on every new machine, and again after you change the module.

## 3. Run the host

```bash
dotnet run --project src/GameServer.Host
```

`launchSettings.json` selects the Development environment and `http://localhost:5000`.
Tables are created and upgraded automatically at startup (migrations are tracked in `schema_migrations`).

## Check that it works

Open these in a browser:

| URL | What |
|---|---|
| <http://localhost:5000/healthz> | Status and the RPCs that your module registered |
| <http://localhost:5000/admin> | Admin panel. Key in Development: `dev-admin-key-do-not-use-in-production-0123456789` |
| <http://localhost:5000/playground/> | Web playground: every player endpoint with a "Send" button |
| <http://localhost:5000/arena/> | The browser version of the Tank Arena demo |

## The game config (for the demo game)

Nothing to do. Shop, quests and match modes ship with the module as `config/*.json`. When the server starts it writes the keys
that are missing into the database (look for `Game config '...': written` in the log). If you changed the files and want them to
win over the database, start with `GameServer__GameConfig__Mode=sync`. See [Game config files](../server/game-config).

Publish the module again after a change (`dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules`), the
files are copied from there.

## Next

- [Your first request](./first-request)
- Ready for a real server? [Install the server](../install/)

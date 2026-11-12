# Run the server

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

## Write the game config (for the demo game)

Shop, quests and match modes live in the **database**, not in git. For the sample game open
<http://localhost:5000/arena/> once and press **Write game config** (it asks for the admin key). See [Tank Arena](../guides/tank-arena).

## Next

[Your first request](./first-request)

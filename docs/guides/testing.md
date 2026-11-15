# Testing

## One command for the server and SDK

`run-tests.ps1` (Windows) or `run-tests.sh` (Linux/macOS) does everything:

1. builds the solution (also the SDK smoke test and the Arena bots)
2. runs the unit tests (`tests/GameServer.Core.Tests`, no server or database needed)
3. starts PostgreSQL with Docker
4. publishes the sample module
5. starts the server
6. runs `tests/GameServer.SmokeTest`: login, hooks, friends, clans, chat over websocket and tcp, parties, matchmaker and matches
   over websocket, shop, stats/achievements/quests/daily reward, live notification event, admin API, admin console (roles, audit,
   dashboard, storage, live-ops, modules), mute, rate limit, account deletion
7. runs the SDK smoke test and the Arena bots (8 bots)
8. writes everything into `logs/REPORT.txt`

```bash
powershell -ExecutionPolicy Bypass -File .\run-tests.ps1     # Windows
./run-tests.sh                                                # Linux / macOS
```

## Unit tests only

```bash
dotnet test tests/GameServer.Core.Tests
```

They need nothing running (no server, no database). They cover:

- the real services with in-memory stores and a fake clock: shop limits and their reset at midnight, sale windows, the daily
  reward streak (with a UTC offset and missed days), stats, achievements and daily quests, account deletion steps
- the pure rules: period keys and resets, login method choice (`GameServer:Auth:Providers`), game config files, admin rules
- the wiring: the whole Core + Postgres container is built with `ValidateOnBuild`, and Core must not reference Npgsql

How the fakes work: [Server architecture](/server/architecture#testing-a-service).

## Against a running server

```bash
dotnet run --project tests/GameServer.SmokeTest -- http://localhost:5000 <adminKey>
```

The log is `smoke-test.log`. It creates three throw-away players and deletes them at the end.

## Unity tests

PlayMode tests in `samples/unity-client/Assets/Tests/PlayMode/GameTours.cs` tour the UI and save screenshots into `logs/shots`:
`MenuTour`, `PlayOneMatch`, `PopupTour`, `ProfileTour`, `InventoryTour`, `ChatTour`, `ChatThreadsTour`. Headless helpers:
`ArenaBots.Run` and `GameBuild.SmokeBoot`.

::: warning Unity cannot open one project twice
If the editor has the project open, batch runs must use a **copy** of the project. Keep the copy on a short path: under a deep
temp folder the package import fails (paths over 260 characters) and Boot shows
"The referenced script (Unknown) on this Behaviour is missing". The copy needs `sdk/unity` three folders up, like the repo.
:::

The tests use the guest account saved in the editor's PlayerPrefs.

## Not covered

A real phone (the SDK smoke test covers matches over UDP), and the Postgres stores on their own: they are tested only through
the smoke test against a running database.

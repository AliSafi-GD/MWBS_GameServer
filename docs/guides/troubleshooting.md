# Troubleshooting

## The server does not start

| Message / symptom | Fix |
|---|---|
| Refuses to start without `GameServer__Jwt__Secret` | Set a secret of at least 32 bytes (`JWT_SECRET` in `.env`) |
| `ConnectionStrings__Default` missing | Set the connection string, or use Development |
| Database connection refused | Is Postgres running? (`docker compose ps`). Is port 5432 taken by a local PostgreSQL? Use another port ([Run the server](../getting-started/run-the-server)) |
| "transport ... not provided" | `Realtime:Transports` or `Match:Transports` names a protocol with no plugin registered in `Program.cs` |
| Module not found | The folder `modules/<Name>/` must contain `<Name>.dll` (same name). Run `dotnet publish` again |
| `/healthz` shows no RPCs | The module did not load. Check the server log |

## The client cannot connect

| Symptom | Fix |
|---|---|
| `network_error` on a phone | `localhost` is the phone itself. Use the computer's LAN address, listen on `0.0.0.0`, open the firewall |
| `network_error` with `https` | Certificate or proxy problem. Try plain `http` on a LAN first |
| `maintenance` | Maintenance mode is on in the admin panel |
| `app_version` | Your build is older than the `minimum` in `app_version` |
| 401 on every call | The access token expired and the refresh failed. Log in again (`SessionEnded`) |
| 429 | Rate limited. Back off |
| WebSocket drops behind a proxy | The proxy must forward the upgrade for `/ws` and `/match`, with a long idle timeout |

## Matches

| Symptom | Fix |
|---|---|
| "unknown mode" | Press **Write game config**, or define the mode in the `matches` config |
| Match found but the client cannot connect | You have about 30 seconds to connect after `MatchFound`. Check the transport (`udp` needs LiteNetLib and the define) |
| Nobody is matched | `minPlayers` more than the players waiting. After `fillSeconds` a smaller match may start |
| Reconnect fails | The handler's grace time passed (15 s in the sample) |

## Unity

| Symptom | Fix |
|---|---|
| "The referenced script (Unknown) on this Behaviour is missing" in a copy of the project | The path is too long. Move the copy to a short path |
| Compile errors about `Newtonsoft.Json` | Let Package Manager install `com.unity.nuget.newtonsoft-json` 3.2.1 |
| Events fire but touching a GameObject throws | `GameClient` was created off the main thread |
| Editor hangs on a network call | You used `.Result` or `.Wait()`. Use `await` |

## Chat

| Symptom | Fix |
|---|---|
| Lobby history is empty | `GameServer:Chat:PersistRooms` is `false` by default |
| `muted` | An admin muted the player |
| Message refused | A `chat.send` hook threw (for example the sample module blocks `http://` links) |

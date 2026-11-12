# Run with Docker

Docker Compose starts both PostgreSQL and the server. Use it for staging and production-like runs.

```bash
cp .env.example .env              # then edit JWT_SECRET, ADMIN_KEY and DB_PASSWORD
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules
docker compose up --build
```

The server answers on <http://localhost:7350> and the admin panel is at `/admin`.

## What compose exposes

| Host port | Container | Used when |
|---|---|---|
| `7350` | `8080` HTTP | always |
| `7351` | TCP realtime | `REALTIME_TRANSPORTS` includes `tcp` |
| `7360/udp` | UDP matches | `MATCH_TRANSPORTS` includes `udp` |
| `127.0.0.1:5432` | PostgreSQL | local access only |

`./modules` is mounted read-only at `/modules`. Only `./modules` and the values in `.env` change between games:
**one copy of the compose folder per game**.

## `.env` values

| Variable | Meaning |
|---|---|
| `JWT_SECRET` | At least 32 characters. Generate with `openssl rand -base64 48` |
| `ADMIN_KEY` | At least 24 characters. Leave empty to disable the admin panel and API |
| `DB_PASSWORD` | Random password **without** `;` or `=` (it goes into a connection string) |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Google Play Games login. Leave empty to disable |
| `REALTIME_TRANSPORTS` | `websocket`, `tcp` or `websocket,tcp` |
| `MATCH_TRANSPORTS` | `websocket`, `udp` or `websocket,udp` |

Outside Development the server refuses to start without `GameServer__Jwt__Secret` (min 32 bytes) and
`ConnectionStrings__Default`.

## Updating a module

```bash
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules
docker compose restart server
```

# Settings and environment

Defaults from `src/GameServer.Host/appsettings.json`. Override in `appsettings.{Environment}.json` or with environment
variables (`GameServer__Jwt__Secret`).

```json
{
  "GameServer": {
    "ModulesPath": "modules",
    "GameConfig": { "Path": "", "Mode": "seed" },
    "ForwardedHeaders": { "Enabled": false, "KnownProxies": "" },
    "Jwt": { "Issuer": "gameserver", "ExpiryMinutes": 60, "RefreshDays": 30 },
    "Realtime": {
      "Transports": "websocket",
      "MaxMessageBytes": 16384,
      "AuthTimeoutSeconds": 10,
      "IdleTimeoutSeconds": 120,
      "MaxConnectionsPerUser": 5,
      "WebSocket": { "Path": "/ws" },
      "Tcp": { "Address": "0.0.0.0", "Port": 7351, "CertificatePath": "", "CertificatePassword": "" }
    },
    "Match": {
      "Transports": "websocket",
      "MaxMessageBytes": 16384,
      "TokenSeconds": 30,
      "MatchmakerIntervalMs": 1000,
      "MaxTicketsPerUser": 3,
      "MaxMatches": 10000,
      "WebSocket": { "Path": "/match" },
      "Udp": { "Port": 7360, "PublicHost": "", "MaxConnections": 2000, "DisconnectTimeoutMs": 8000 }
    },
    "Chat": {
      "PersistRooms": false, "RetentionDays": 30, "MaxTextLength": 1000,
      "MessagesPerWindow": 10, "WindowSeconds": 10, "DirectMessagesFriendsOnly": false
    },
    "Parties": { "MaxSize": 8, "GraceSeconds": 15, "InviteMinutes": 5, "RequireConnection": true },
    "Auth": {
      "Providers": "",
      "GooglePlay": { "ClientId": "", "ClientSecret": "" },
      "Phone": { "Enabled": false, "CodeTtlSeconds": 300, "CooldownSeconds": 60, "MaxAttempts": 5 }
    }
  }
}
```

Not in the file (secrets, set them yourself): `GameServer:Jwt:Secret`, `GameServer:Admin:Key`, `ConnectionStrings:Default`.

## `.env` for Docker Compose

| Variable | Maps to |
|---|---|
| `JWT_SECRET` | `GameServer__Jwt__Secret` |
| `ADMIN_KEY` | `GameServer__Admin__Key` |
| `DB_PASSWORD` | the Postgres password in `ConnectionStrings__Default` |
| `AUTH_PROVIDERS` | `GameServer__Auth__Providers` |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | `GameServer__Auth__GooglePlay__*` |
| `REALTIME_TRANSPORTS` | `GameServer__Realtime__Transports` |
| `MATCH_TRANSPORTS` | `GameServer__Match__Transports` |
| `GAME_CONFIG_MODE` | `GameServer__GameConfig__Mode` |
| `FORWARDED_HEADERS` | `GameServer__ForwardedHeaders__Enabled` |

## Game config {#game-config}

| Setting | Meaning |
|---|---|
| `GameServer:GameConfig:Mode` | `seed` (default, also when empty): write only the keys missing in the database. `sync`: files win on every start. `off`: ignore the files |
| `GameServer:GameConfig:Path` | Optional extra folder with `<key>.json` files, besides `modules/<Name>/config/`. Empty = none |

The files themselves are described in [Game config files](../server/game-config).

## Relative paths

`GameServer:ModulesPath` and `GameServer:GameConfig:Path` may be relative. They are resolved from the host's **content root**,
not from the process working directory:

| How it runs | Content root |
|---|---|
| `dotnet run --project src/GameServer.Host` | the project folder `src/GameServer.Host` (so `../../modules` in `appsettings.Development.json` is the repository's `modules/`) |
| Windows Service | the folder of `GameServer.Host.exe` (the service's working directory is `C:\Windows\System32`) |
| `dotnet GameServer.Host.dll` / systemd / Docker | the current directory (`WorkingDirectory=` in a unit, `/app` in the image) |

An absolute path is always the clearest choice for a service, and the Docker image sets `GameServer__ModulesPath=/modules`.

## Behind a reverse proxy

| Setting | Meaning |
|---|---|
| `GameServer:ForwardedHeaders:Enabled` | `false` by default. When `true` the server trusts `X-Forwarded-For` and `X-Forwarded-Proto` (one proxy hop), so rate limits and logs use the real player IP |
| `GameServer:ForwardedHeaders:KnownProxies` | Optional, comma separated proxy IPs to trust, e.g. `10.0.0.5,10.0.0.6`. Empty = trust whichever proxy connects |

Enable it **only** when the proxy is the only way to reach the server (port bound to `127.0.0.1` or closed in the firewall).
If the port is also reachable directly, set `KnownProxies` to the proxy's address, or leave it off. See
[Put it on a VPS](../install/vps#https).

## Running as a service

The host runs as a Windows Service or a systemd unit (`Type=notify`) with no extra setting. See
[Windows](../install/windows) and [Linux](../install/linux). `GameServer__ModulesPath` is best an absolute path there,
and `ASPNETCORE_URLS` (e.g. `http://0.0.0.0:7350`) sets the address, because the default is `localhost` only.

## Ports

| Port | Use |
|---|---|
| 5000 | Development HTTP (`launchSettings.json`) |
| 7350 | HTTP in Docker (container 8080) |
| 7351 | Realtime TCP (optional) |
| 7360/udp | UDP matches (optional) |
| 5432 | PostgreSQL (bound to 127.0.0.1) |

## Environment name

`ASPNETCORE_ENVIRONMENT=Development` loads the dev defaults. Anything else needs the required secrets set.

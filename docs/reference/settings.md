# Settings and environment

Defaults from `src/GameServer.Host/appsettings.json`. Override in `appsettings.{Environment}.json` or with environment
variables (`GameServer__Jwt__Secret`).

```json
{
  "GameServer": {
    "ModulesPath": "modules",
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
| `FORWARDED_HEADERS` | `GameServer__ForwardedHeaders__Enabled` |

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
[Windows](../install/windows) and [Linux](../install/linux). `GameServer__ModulesPath` should be an absolute path there,
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

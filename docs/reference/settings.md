# Settings and environment

Defaults from `src/GameServer.Host/appsettings.json`. Override in `appsettings.{Environment}.json` or with environment
variables (`GameServer__Jwt__Secret`).

```json
{
  "GameServer": {
    "ModulesPath": "modules",
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
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | `GameServer__Auth__GooglePlay__*` |
| `REALTIME_TRANSPORTS` | `GameServer__Realtime__Transports` |
| `MATCH_TRANSPORTS` | `GameServer__Match__Transports` |

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

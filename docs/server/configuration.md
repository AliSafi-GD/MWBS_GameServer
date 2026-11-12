# Configuration

The server reads ASP.NET Core configuration: `appsettings.json`, then `appsettings.{Environment}.json`, then environment
variables. In an environment variable use `__` for nesting: `GameServer:Jwt:Secret` becomes `GameServer__Jwt__Secret`.

There are two different kinds of configuration. Do not mix them up:

| | Server settings | Remote config |
|---|---|---|
| Where | `appsettings*.json`, environment | Database table `config`, edited in the admin panel |
| Holds | Secrets, ports, transports, limits | Game content: shop, quests, match modes, version rules |
| Change needs | Restart | Nothing, applies in about 5 seconds |
| Reaches clients | Never | Public keys, via `GET /v1/config` |

This page is about the first kind. For the second see [Remote config](./remote-config).

## The important settings

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Default` | none | PostgreSQL connection string. Required outside Development |
| `GameServer:ModulesPath` | `modules` | Folder with `<Name>/<Name>.dll` modules |
| `GameServer:Jwt:Secret` | none | Signing key, at least 32 bytes. Required outside Development |
| `GameServer:Jwt:ExpiryMinutes` | `60` | Lifetime of the access token |
| `GameServer:Jwt:RefreshDays` | `30` | Lifetime of the refresh token |
| `GameServer:Admin:Key` | empty | Admin key (min 24 characters). Empty disables the panel and API |
| `GameServer:Realtime:Transports` | `websocket` | `websocket`, `tcp` or both |
| `GameServer:Match:Transports` | `websocket` | `websocket`, `udp` or both. The first is the default |
| `GameServer:Chat:PersistRooms` | `false` | Store room messages. Direct and clan messages are always stored |
| `GameServer:Chat:RetentionDays` | `30` | How long chat history is kept |
| `GameServer:Parties:MaxSize` | `8` | Default party size |
| `GameServer:Auth:GooglePlay:ClientId` / `ClientSecret` | empty | Enables Google Play Games login |
| `GameServer:Auth:Phone:Enabled` | `false` | Enables phone (OTP) login |

The full list with all defaults is in [Settings and environment](../reference/settings).

## Development defaults

`appsettings.Development.json` (loaded when `ASPNETCORE_ENVIRONMENT=Development`) provides a local database, a JWT secret,
the admin key and turns phone login on, so `dotnet run` works without setup.

::: danger Dev keys are public
These values are in a public repository. A server that uses them can be taken over by anyone. In production always set your own.
:::

## Passing settings in Docker

`docker-compose.yml` maps `.env` values to the right settings (`JWT_SECRET` to `GameServer__Jwt__Secret`, and so on).
See [Run with Docker](../getting-started/docker).

# Deploy to production

A checklist for putting a server on the internet.

## 1. Secrets

- [ ] `JWT_SECRET`: a long random string (`openssl rand -base64 48`). Different for every game.
- [ ] `ADMIN_KEY`: another random string, 24+ characters, or empty to disable the admin panel.
- [ ] `DB_PASSWORD`: random, without `;` or `=`.
- [ ] Never run with the keys from `appsettings.Development.json` or the Postman files. They are in a public repository.

## 2. HTTPS

Put a reverse proxy (Caddy, nginx, Traefik) in front and terminate TLS. Clients then use `https://` and `wss://`.

- The proxy must forward the **WebSocket upgrade** for `/ws` and `/match`.
- Enable forwarded headers so rate limiting counts the real client IP.
- UDP (matches) and TCP (realtime) are not proxied by an HTTP proxy. Open those ports directly if you enable them
  and set `GameServer:Match:Udp:PublicHost` to the public address.

## 3. Remove what you do not need

- Delete `src/GameServer.Host/wwwroot/playground/` (the web playground) and `wwwroot/arena/` from your production build.
- Restrict `/admin` to your IP or VPN at the proxy.

## 4. Database

- Back up the `pgdata` volume. Everything (accounts, wallet, ledger, config) is there.
- Do not edit applied migrations; add a new numbered one.
- Do not expose PostgreSQL to the internet. The Compose file binds it to `127.0.0.1`.

## 5. Modules

`modules/` is mounted read-only into the container. Publish your module into it and restart the server:

```bash
dotnet publish MyGame.Modules -c Release -o modules/MyGame.Modules
docker compose restart server
```

## 6. Know the limits

| Limit | Meaning |
|---|---|
| Single instance | Matches and parties live in memory on the instance that created them. There is no multi-server routing yet |
| No chat backplane | Chat fan-out is per instance. A Redis/Postgres backplane is not built |
| SMS | Phone login needs your own `ISmsSender` |
| Real-money purchases | Not built. Use the in-game shop |

## 7. Logs and health

`GET /healthz` returns the status and the loaded RPCs. Use it for the container health check and uptime monitoring.

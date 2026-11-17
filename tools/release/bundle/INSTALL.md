# GameServer __VERSION__ (__RID__)

Everything needed to run the server is in this folder: .NET is included (self-contained), nothing is downloaded.

## 1. Settings

The server reads its settings from environment variables. The two you always need:

| Variable | What |
|---|---|
| `ConnectionStrings__Default` | PostgreSQL, e.g. `Host=localhost;Database=game;Username=game;Password=...` |
| `GameServer__Jwt__Secret` | at least 32 random characters |

Optional: `GameServer__Admin__Key` (24+ characters; turns on the admin panel at `/admin`), `ASPNETCORE_URLS` (default `http://localhost:5000`),
`GameServer__Auth__Providers`, `GameServer__Realtime__Transports`, `GameServer__Match__Transports`. The full list is in the
docs, "Settings".

## 2. Your game

Put the published game module in `modules/<Name>/` (the DLL and its `config/` folder). How to write one: `sdk/README.md`.

## 3. Run

Linux / macOS:

```bash
chmod +x GameServer.Host
./GameServer.Host
```

Windows:

```powershell
.\GameServer.Host.exe
```

The database tables are created at the first start. Check `http://localhost:5000/healthz`.

To run it as a service (systemd, Windows Service) and behind Caddy, see the docs: "Install the server".

## Needed on Linux

The distribution must have `libicu` (ICU) and OpenSSL, which almost every server distribution has. Without ICU, set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

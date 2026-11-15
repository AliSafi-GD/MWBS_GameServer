# Docker Compose

Docker Compose starts PostgreSQL and the server together, keeps them running and restarts them after a reboot.
The steps are the same on Linux, Windows and macOS. Where the commands differ, both versions are shown.

## 1. Install Docker

| System | What to install |
|---|---|
| **Windows 10/11** | [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) (uses WSL 2; the installer offers to set it up). Restart if asked |
| **macOS** | [Docker Desktop](https://docs.docker.com/desktop/setup/install/mac-install/) |
| **Linux** | Docker Engine, see below |

On **Ubuntu or Debian** the official convenience script installs Docker Engine and the Compose plugin:

```bash
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
sudo usermod -aG docker $USER      # lets you run docker without sudo; log out and in again afterwards
```

Other Linux distributions: follow the [official guide](https://docs.docker.com/engine/install/). Then check:

```bash
docker --version
docker compose version
```

You also need [Git](https://git-scm.com/downloads) (`sudo apt install git` on Ubuntu).

## 2. Get the files

```bash
git clone https://github.com/AliSafi-GD/MWBS_GameServer.git
cd MWBS_GameServer
```

## 3. Create the `.env` file

`.env` holds your secrets. It is not in git. Start from the example:

::: code-group
```bash [Linux / macOS]
cp .env.example .env
```
```powershell [Windows PowerShell]
Copy-Item .env.example .env
```
:::

Now make three random values and put them in `.env`.

::: code-group
```bash [Linux / macOS]
echo "JWT_SECRET=$(openssl rand -base64 48)"
echo "ADMIN_KEY=$(openssl rand -base64 36)"
echo "DB_PASSWORD=$(openssl rand -hex 16)"
```
```powershell [Windows PowerShell]
function New-RandomText($bytes) {
  $b = New-Object byte[] $bytes
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  [Convert]::ToBase64String($b)
}
"JWT_SECRET=" + (New-RandomText 48)
"ADMIN_KEY=" + (New-RandomText 36)
"DB_PASSWORD=" + ((New-RandomText 24) -replace '[^A-Za-z0-9]', '')
```
:::

Copy the three printed lines into `.env`, replacing the lines that are already there (open it with Notepad, `nano` or any
editor). That is all you must change.

::: warning Set `DB_PASSWORD` before the first start
PostgreSQL stores the password the first time it starts. Changing `DB_PASSWORD` in `.env` later does not change it in the
existing database.
:::

The other values (login methods, transports) have safe defaults. They are all in the [table below](#env-values).

## 4. Build your game module

A module is a DLL in `modules/<Name>/<Name>.dll`. The folder name must equal the DLL name. `modules/` is not in git, so do
this once on every new machine, and again when you change the module. The commands build the sample module; for your own
game use your own project path.

**Option A: you have the .NET 10 SDK**

```bash
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules
```

**Option B: you do not want to install .NET.** Let a Docker container build it:

::: code-group
```bash [Linux / macOS]
docker run --rm -v "$PWD":/repo -w /repo mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules
```
```powershell [Windows PowerShell]
docker run --rm -v "${PWD}:/repo" -w /repo mcr.microsoft.com/dotnet/sdk:10.0 `
  dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules
```
:::

On Linux the files created this way belong to root. That is fine (the server only reads them). Run
`sudo chown -R $USER .` if you want to edit them yourself.

## 5. Start

```bash
docker compose up -d
```

The first run downloads PostgreSQL and builds the server image, which takes a few minutes. After that it starts in seconds.
`-d` means "in the background".

## 6. Check that it works

```bash
docker compose ps                     # both services should say "running"
curl http://localhost:7350/healthz
```

You should get `{"status":"ok","rpcs":[...]}` with the RPCs of your module. If `rpcs` is empty the module was not found:
see [Troubleshooting](../guides/troubleshooting). In a browser:

| URL | What |
|---|---|
| <http://localhost:7350/healthz> | Status and loaded RPCs |
| <http://localhost:7350/admin> | Admin panel. Sign in with your `ADMIN_KEY` |
| <http://localhost:7350/arena/> | Browser version of the Tank Arena demo (also where you write the game config) |

On a remote machine replace `localhost` with its address.

## 7. Write the game config (once)

Shop, quests and match modes live in the **database**, not in git. For the sample game open
`http://localhost:7350/arena/`, press **Write game config** and enter your admin key. You do this once per database. See
[Tank Arena](../guides/tank-arena).

## Everyday commands

| What | Command |
|---|---|
| See the logs | `docker compose logs -f server` (Ctrl+C stops watching, not the server) |
| Stop | `docker compose stop` |
| Start again | `docker compose start` |
| Restart the server | `docker compose restart server` |
| Update the server code | `git pull`, then `docker compose up -d --build` |
| Update your module | publish it again (step 4), then `docker compose restart server` |
| Stop and remove the containers | `docker compose down` (the database is kept) |
| Delete everything including the database | `docker compose down -v` (cannot be undone) |

Both services use `restart: unless-stopped`, so they come back after a reboot as long as Docker itself starts with the
machine (it does by default on Linux; on Windows and macOS Docker Desktop must be set to start at login).

## What compose exposes

| Host port | Container | Used when |
|---|---|---|
| `7350` | `8080` HTTP | always |
| `7351` | TCP realtime | `REALTIME_TRANSPORTS` includes `tcp` |
| `7360/udp` | UDP matches | `MATCH_TRANSPORTS` includes `udp` |
| `127.0.0.1:5432` | PostgreSQL | this machine only |

`./modules` is mounted read-only at `/modules`. Only `./modules` and the values in `.env` change between games:
**one copy of the compose folder per game**.

## `.env` values {#env-values}

| Variable | Meaning |
|---|---|
| `JWT_SECRET` | At least 32 characters. Generate with `openssl rand -base64 48` |
| `ADMIN_KEY` | At least 24 characters. Leave empty to disable the admin panel and API |
| `DB_PASSWORD` | Random password **without** `;` or `=` (it goes into a connection string) |
| `AUTH_PROVIDERS` | Login methods, e.g. `device` or `device,googleplay`. Empty = guest plus whatever is configured |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Google Play Games login. Leave empty to disable |
| `REALTIME_TRANSPORTS` | `websocket`, `tcp` or `websocket,tcp` |
| `MATCH_TRANSPORTS` | `websocket`, `udp` or `websocket,udp` |
| `FORWARDED_HEADERS` | `true` only behind a reverse proxy that is the only way in. See [Put it on a VPS](./vps#https) |

Outside Development the server refuses to start without `GameServer__Jwt__Secret` (min 32 bytes) and
`ConnectionStrings__Default`; compose sets both from `.env`.

## Next

- Reach the server from the internet: [Put it on a VPS](./vps)
- [Your first request](../getting-started/first-request)
- The full checklist: [Deploy to production](../server/production)

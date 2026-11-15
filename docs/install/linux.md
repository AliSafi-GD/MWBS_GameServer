# Linux binary (Ubuntu / Debian)

Install the server without Docker: .NET and PostgreSQL from packages, the server as a systemd service. The steps are
written for Ubuntu 22.04/24.04 and Debian 12. Run them as a normal user with `sudo`.

If you can use Docker, [Docker Compose](./docker-compose) is shorter.

## 1. Install .NET 10 and Git

Microsoft's official `dotnet-install` script works on every supported distribution and does not depend on which .NET
versions your Ubuntu repositories carry:

```bash
sudo apt update
sudo apt install -y git curl ca-certificates libicu-dev
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
sudo bash dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
dotnet --version        # 10.x
```

This installs the SDK, which you need to build the server. (If your distribution already offers it, `sudo apt install dotnet-sdk-10.0`
does the same. See the [official guide](https://learn.microsoft.com/dotnet/core/install/linux).)

## 2. Install PostgreSQL

```bash
sudo apt install -y postgresql
sudo systemctl enable --now postgresql
```

Create the database and a user. The password must not contain `;` or `=` (it goes into a connection string), so use a
hex string:

```bash
DB_PASS=$(openssl rand -hex 16)
sudo -u postgres psql -c "CREATE USER game WITH PASSWORD '$DB_PASS';"
sudo -u postgres psql -c "CREATE DATABASE game OWNER game;"
echo "$DB_PASS"        # keep it, you need it in step 5 (the same terminal still has it in $DB_PASS)
```

Tables are created by the server on its first start.

## 3. Get the code and build

```bash
git clone https://github.com/AliSafi-GD/MWBS_GameServer.git
cd MWBS_GameServer

sudo mkdir -p /opt/gameserver
sudo chown $USER /opt/gameserver          # so you can publish into it without sudo
dotnet publish src/GameServer.Host -c Release -o /opt/gameserver
dotnet publish samples/HelloGame.Modules -c Release -o /opt/gameserver/modules/HelloGame.Modules
```

The server looks for modules in the folder set by `GameServer:ModulesPath` (environment variable
`GameServer__ModulesPath`). Each module is `modules/<Name>/<Name>.dll`, and the folder name must equal the DLL name.
Use an absolute path in a service, so it does not depend on the folder the server is started from. (A relative path is
resolved from the server's content root, which under systemd is its `WorkingDirectory`.) For your own game publish your
own project into `/opt/gameserver/modules/<YourModule>`.

## 4. A user for the server

The server should not run as root:

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin gameserver
```

## 5. The settings file

Settings go in one file that only root and the service can read:

```bash
sudo tee /etc/gameserver.env > /dev/null <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:7350
GameServer__ModulesPath=/opt/gameserver/modules
GameServer__Jwt__Secret=$(openssl rand -base64 48)
GameServer__Admin__Key=$(openssl rand -base64 36)
ConnectionStrings__Default=Host=localhost;Database=game;Username=game;Password=$DB_PASS
EOF
sudo chown root:gameserver /etc/gameserver.env
sudo chmod 640 /etc/gameserver.env
```

(If you opened a new terminal after step 2, set `DB_PASS=<the password>` first, because the command above reads it.)

| Setting | Meaning |
|---|---|
| `ASPNETCORE_ENVIRONMENT=Production` | Never `Development` on a real server: it turns on public dev keys |
| `ASPNETCORE_URLS` | Where to listen. Without it the server only answers on `localhost` |
| `GameServer__Jwt__Secret` | Required, at least 32 characters |
| `GameServer__Admin__Key` | At least 24 characters, or leave it out to disable the admin panel |
| `ConnectionStrings__Default` | Required. The database from step 2 |

More settings (login methods, transports) are in [Settings and environment](../reference/settings): add them to the same
file as `GameServer__Auth__Providers=device,googleplay` and so on.

## 6. First run in the foreground

Try it by hand first. If something is wrong you see the message right away:

```bash
sudo bash -c 'set -a; . /etc/gameserver.env; set +a; cd /opt/gameserver && dotnet GameServer.Host.dll'
```

You should see `Loaded module ...` lines and `Now listening on: http://0.0.0.0:7350`. In a second terminal:

```bash
curl http://localhost:7350/healthz
```

Stop the server with Ctrl+C. If it stops with an error about the secret or the connection string, check
`/etc/gameserver.env` (a closed or wrong database gives `Failed to connect to 127.0.0.1:5432`).

## 7. Run it as a service

```bash
sudo tee /etc/systemd/system/gameserver.service > /dev/null <<'EOF'
[Unit]
Description=GameServer
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=notify
User=gameserver
WorkingDirectory=/opt/gameserver
EnvironmentFile=/etc/gameserver.env
ExecStart=/usr/bin/dotnet /opt/gameserver/GameServer.Host.dll
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=gameserver

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now gameserver
sudo systemctl status gameserver
```

`enable` starts it at every boot, `--now` starts it right away. `Type=notify` works because the server reports to systemd
when it is ready.

## 8. Open the port

If you use the Ubuntu firewall (`ufw`), allow SSH first, then the game port:

```bash
sudo ufw allow OpenSSH
sudo ufw allow 7350/tcp
sudo ufw enable
sudo ufw status
```

Add `7351/tcp` if you use the TCP chat transport, and `7360/udp` for UDP matches. Many VPS providers also have their own
firewall in the web console: open the port there too.

## 9. Check that it works

From any computer: `curl http://<server-address>:7350/healthz` should print `{"status":"ok","rpcs":[...]}`.
The admin panel is `http://<server-address>:7350/admin` (sign in with `GameServer__Admin__Key`).

The game config needs no extra step: the module's `config/*.json` is written into the database when the server starts
(only keys that are missing; see `GameServer__GameConfig__Mode` in [Game config files](../server/game-config)). Look for
`Game config '...': written` in `journalctl -u gameserver`.

## Everyday commands

| What | Command |
|---|---|
| Logs (live) | `sudo journalctl -u gameserver -f` |
| Logs since the last boot | `sudo journalctl -u gameserver -b` |
| Restart / stop / start | `sudo systemctl restart gameserver` (or `stop`, `start`) |
| Update the server | `git pull`, `sudo systemctl stop gameserver`, publish again (step 3), `sudo systemctl start gameserver` |
| Update your module | publish it into `/opt/gameserver/modules/<Name>`, then `sudo systemctl restart gameserver` |
| Change a setting | edit `/etc/gameserver.env`, then `sudo systemctl restart gameserver` |

Back up the database with `sudo -u postgres pg_dump game > game-backup.sql`.

## Next

- HTTPS and a domain name: [Put it on a VPS](./vps#https)
- The full checklist: [Deploy to production](../server/production)

# Put it on a VPS

A VPS is a small computer you rent that is always on and has a public address. This page takes you from nothing to a game
server your players can reach. No earlier server experience needed.

## 1. Rent a small server

Any provider works (Hetzner, DigitalOcean, Vultr, Linode, a local provider...). Choose:

- **Ubuntu 24.04 LTS** (or 22.04)
- **1 CPU, 2 GB RAM** is enough to start (1 GB works for a test)
- A location near your players
- Your **SSH key** if the provider asks for one, or a root password

Write down the server's public IP address. Below it is called `203.0.113.10`.

## 2. Connect with SSH

On Windows 10/11, macOS and Linux open a terminal (PowerShell on Windows) and run:

```bash
ssh root@203.0.113.10
```

Answer `yes` to the fingerprint question. You are now typing commands on the server.

Optional but wise: create your own user instead of working as root, and update the system.

```bash
apt update && apt upgrade -y
adduser deploy                  # asks for a password
usermod -aG sudo deploy
su - deploy                     # continue as this user, put sudo before admin commands
```

## 3. Install the server

Follow [Docker Compose](./docker-compose) from step 1 (the Ubuntu commands are there). In short:

```bash
curl -fsSL https://get.docker.com -o get-docker.sh && sudo sh get-docker.sh
sudo usermod -aG docker $USER   # then log out and SSH in again
git clone https://github.com/AliSafi-GD/MWBS_GameServer.git && cd MWBS_GameServer
cp .env.example .env            # put your three random values in it (see the Docker Compose page, step 3)
```

Build your module (step 4 of that page; the Docker option means you do not install .NET on the VPS), then
`docker compose up -d`. Prefer no Docker? Use the [Linux binary](./linux) page instead.

## 4. Open the port

The game listens on port **7350**. Allow it in the Ubuntu firewall, and in the provider's firewall if it has one (most have a
"Firewall" or "Network" page in their web console):

```bash
sudo ufw allow OpenSSH
sudo ufw allow 7350/tcp
sudo ufw enable
```

::: warning Docker and ufw
Docker publishes ports by itself and skips `ufw`. A port that is published in `docker-compose.yml` is open to the internet
even if `ufw` does not allow it. To really close one, bind it to localhost (see [HTTPS](#https) below) or use the provider's
firewall.
:::

Test from your own computer: `http://203.0.113.10:7350/healthz` should show `{"status":"ok",...}`. The game config is written by the server itself
when it starts, see [Docker Compose, step 7](./docker-compose#_7-the-game-config).

## 5. Point the game at your server

The Unity client needs the server's address (the "base URL"). Plain HTTP and the IP address are fine for testing:
`http://203.0.113.10:7350`. For a real release use HTTPS and a domain (next section).

- **In your own code:** it is `GameClientOptions.BaseUrl` when you create the client, see [Quick start](../client/quick-start).
  Put it in one place (a config constant or a field you can change in the Inspector).
- **In the Tank Arena demo:** on the Boot screen press the gear at the bottom right and type the address. For an Android build
  set `GAME_SERVER_URL` before building so the address is baked in, see [Build an Android APK](../guides/android).

Unity builds block plain `http://` addresses by default (the *Allow downloads over HTTP* player setting), one more reason to use HTTPS for a real game.

## HTTPS with a domain {#https}

HTTPS needs a domain name. Buy one anywhere, then add an **A record** that points `game.example.com` to your server's IP.
Wait a few minutes until `ping game.example.com` shows your IP.

[Caddy](https://caddyserver.com) is a small web server that gets and renews the HTTPS certificate by itself. It sits in front
of the game server and forwards everything to it, including WebSockets (chat and matches), with no extra settings.

**1. Install Caddy** (Ubuntu, official packages):

```bash
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update
sudo apt install caddy
```

**2. Write the Caddyfile.** Replace the file `/etc/caddy/Caddyfile` with:

```
game.example.com {
    reverse_proxy 127.0.0.1:7350
}
```

then `sudo systemctl reload caddy`. Open ports 80 and 443 (`sudo ufw allow 80,443/tcp`; Caddy needs 80 to get the certificate).

**3. Make the game port private.** Players must only reach the server through Caddy. With Docker Compose create the file
`docker-compose.override.yml` next to `docker-compose.yml`:

```yaml
services:
  server:
    ports: !override
      - "127.0.0.1:7350:8080"
```

This replaces the published ports so 7350 only answers on the VPS itself. (It also drops `7351` and `7360/udp`. Add them
back to the list if you use the TCP or UDP transports; those are not proxied by Caddy and must stay open.)
With the [binary install](./linux) set `ASPNETCORE_URLS=http://127.0.0.1:7350` instead. Then close the port: `sudo ufw delete allow 7350/tcp`.

**4. Tell the server it is behind a proxy.** In `.env` set

```
FORWARDED_HEADERS=true
```

(binary install: `GameServer__ForwardedHeaders__Enabled=true`) and apply it with `docker compose up -d`.

::: danger Why this setting exists, and when it is safe
Behind a proxy every request comes from the proxy's address. Without this setting the server sees one single player and
all your players **share the same rate limit** (for example 20 logins a minute for the whole game). With it, the server reads the real
player address from the `X-Forwarded-For` header that Caddy sets.

Turn it on **only** when the proxy is the only way to reach the server (step 3). If the port is also open to the internet,
anyone can send a fake `X-Forwarded-For` and dodge the limits.
:::

**5. Test and use it.** `https://game.example.com/healthz` should answer with a valid certificate. In the client use
`https://game.example.com` as the base URL. Realtime then uses `wss://` automatically.

Optional: let only your own IP open the admin panel. Add this inside the Caddyfile block, above `reverse_proxy`
(use your own IP address):

```
    @blocked {
        path /admin /admin/*
        not remote_ip 198.51.100.7
    }
    respond @blocked 403
```

The full list of things to check is in [Deploy to production](../server/production).

## What next

- Back up the database regularly, see [Deploy to production](../server/production#_4-database)
- Remove the web playground and arena pages from a real release
- [Deploy to production](../server/production): the full checklist

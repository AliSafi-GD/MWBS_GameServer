# Install the server

This section is for putting a server on a real machine: a VPS, a spare PC, or your own computer. If you only want to
try things on your laptop while you write a game, use [Run the server](../getting-started/run-the-server) instead.

## What you get

A running server is three things working together:

| Part | What it is |
|---|---|
| **The game server** | The `GameServer.Host` program. It answers the REST API, chat and matches, and serves the admin panel |
| **PostgreSQL** | The database. Accounts, wallets, config and everything else live here |
| **Your game module** | A DLL in a `modules/` folder with your own game rules. The sample one is `samples/HelloGame.Modules` |

## Two ways to install

| | **Docker Compose** (recommended) | **Binary** (no Docker) |
|---|---|---|
| Works on | Linux, Windows, macOS | [Ubuntu / Debian](./linux), [Windows](./windows) |
| You install | Docker and Git | .NET 10, PostgreSQL and Git |
| PostgreSQL | Started for you | You install and set it up |
| Starts after a reboot | Yes (`restart: unless-stopped`) | Yes, with a systemd unit or a Windows Service |
| Update | `git pull`, then one command | `git pull`, publish again, restart |
| Best for | Almost everyone | Machines where Docker is not allowed or not wanted |

If you are not sure, pick Docker Compose.

## Pick your page

- [Docker Compose](./docker-compose): Linux, Windows and macOS
- [Linux binary](./linux): Ubuntu / Debian with systemd
- [Windows binary](./windows): Windows 10/11 and Windows Server, as a Windows Service
- [Put it on a VPS](./vps): from "I have never rented a server" to a public address, with optional HTTPS
- [Offline release](./offline): a bundle with the server for every OS, the Docker images and the module SDK, for machines
  without internet (no source code needed)

## What you need for each

**Docker Compose:** Docker, Git, about 2 GB of free disk, and the .NET 10 SDK to build your module (or just Docker again:
there is a way to build the module without installing .NET).

**Binary:** Git, the .NET 10 SDK, PostgreSQL (17 is what the Compose file uses), and root/administrator rights to create a service.

A small machine is enough to start: 1 CPU and 1-2 GB of RAM runs the server and PostgreSQL for a test game.

## The same three steps everywhere

1. Get the files and build your **module**.
2. Give the server its **secrets** (JWT secret, admin key, database password).
3. Start it and open `/healthz`. The game's **config** (shop, quests, match modes) comes from the module's `config/` folder and is written into the database at startup, see [Game config files](../server/game-config).

::: warning Never use the development keys
The keys in `appsettings.Development.json` and the Postman files are in a public repository. Every page here shows how to
make your own.
:::

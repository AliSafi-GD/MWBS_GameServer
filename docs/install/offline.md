# Offline release (no internet)

For machines that cannot reach the internet, or a national network where GitHub, NuGet, Docker Hub and Microsoft's servers
are blocked. The release bundle has everything: the server for each OS, the Docker images, and what a game team needs to
build its module. Nobody has to open the server's source code.

## Make the bundle (on a machine with the repository)

```powershell
pwsh tools/release/build-release.ps1
```

It writes `artifacts/release/<version>/`. It needs no internet if the NuGet packages are already in this machine's cache
(they are after one normal build) and the image `mcr.microsoft.com/dotnet/aspnet:10.0` is in Docker. Options:

| Option | What |
|---|---|
| `-Rids linux-x64,win-x64` | Only these systems (default: all five below) |
| `-SkipDocker` | No Docker part |
| `-IncludeBuildImages` | Also save the .NET base image, so release images can be built on an offline machine |
| `-PostgresImage postgres:17-alpine` | Another PostgreSQL image (keep the same major version as an existing database) |
| `-Version 0.2.0` | Default: the version of `GameServer.Abstractions` |

Nothing is uploaded anywhere. Copy the folder to a USB disk or to a server inside the network.

## What is in it

```
artifacts/release/0.1.0/
  gameserver-0.1.0-linux-x64.tar.gz     Ubuntu, Debian, RHEL, Rocky, Alma, Fedora, ... (x64)
  gameserver-0.1.0-linux-musl-x64.tar.gz Alpine
  gameserver-0.1.0-linux-arm64.tar.gz   ARM servers, Raspberry Pi (64-bit)
  gameserver-0.1.0-win-x64.zip          Windows 10/11, Windows Server
  gameserver-0.1.0-osx-arm64.tar.gz     macOS (Apple silicon), for development
  gameserver-0.1.0-docker/              any Linux with Docker
    docker-compose.yml  .env.example  images.tar.gz  load-images.sh  load-images.ps1  modules/
    build/              (with -IncludeBuildImages) the .NET base image and the release Dockerfile
  examples/HelloGame.Modules/           the sample module, ready to copy into modules/
  SHA256SUMS.txt
```

Every server archive is **self-contained**: .NET is inside, nothing has to be installed. On Linux the distribution needs
`libicu` and OpenSSL, which almost every server distribution has (without ICU set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`).
Each archive also has `sdk/` and an empty `modules/` folder, and an `INSTALL.md`.

## Run it with Docker

```bash
./load-images.sh            # once: loads gameserver:<version> and postgres
cp .env.example .env        # set JWT_SECRET (32+ characters), DB_PASSWORD, ADMIN_KEY (24+ characters, or empty)
docker compose up -d
```

This compose file uses the saved image (no `build:`), so the server never builds anything. PostgreSQL is not published
on a host port. Updating: load the new bundle's images, change nothing else, `docker compose up -d`.

## Run the binary

Unpack the archive for your system, set `ConnectionStrings__Default` and `GameServer__Jwt__Secret`, and start
`GameServer.Host` (`GameServer.Host.exe` on Windows). PostgreSQL has to be installed on that machine or reachable from it.
To run it as a service, follow [Linux binary](./linux) or [Windows binary](./windows) from the step after "publish".

## Write a game module offline

The `sdk/` folder of every archive:

| | |
|---|---|
| `packages/GameServer.Abstractions.<version>.nupkg` | the contract a module compiles against |
| `lib/net10.0/GameServer.Abstractions.dll` (+ `.xml`) | the same as a plain DLL |
| `templates/module/` | a `dotnet new` template |

Once per machine (the .NET 10 SDK must be installed):

```bash
dotnet nuget add source "<release>/sdk/packages" --name gameserver
dotnet new install "<release>/sdk/templates/module"
```

Then:

```bash
dotnet new gameserver-module -n MyGame
cd MyGame
dotnet publish -c Release -o <server>/modules/MyGame.Modules
```

The module references `GameServer.Abstractions` with `ExcludeAssets="runtime"`: the server brings its own copy at run time,
so the module does not ship one. A module built against one version runs on that and every later server version.

If the module uses NuGet packages of its own, they have to be in the machine's cache or in a local folder source too.

## What still needs the internet once

- Installing the .NET 10 SDK and Docker themselves (keep their installers with the bundle).
- Unity: the editor, its license activation and Unity's own packages.
- Making the bundle on a machine whose NuGet cache or Docker images are empty.

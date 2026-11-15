# Windows binary

Install the server without Docker on Windows 10/11 or Windows Server: .NET and PostgreSQL installed natively, the server
running as a Windows Service. Use an **administrator** PowerShell for the steps that say so.

If you can use Docker Desktop, [Docker Compose](./docker-compose) is shorter.

## 1. Install .NET 10 and Git

You need the **.NET 10 SDK** on the machine where you build (it can be this same machine). To only *run* the server on another
machine you need just the **ASP.NET Core Runtime 10**. You do **not** need the Hosting Bundle: that is for IIS, and the server
runs on its own.

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
```

Or download the installers from <https://dotnet.microsoft.com/download> and <https://git-scm.com/downloads>. Open a **new**
PowerShell window and check `dotnet --version` (10.x).

## 2. Install PostgreSQL

Download the installer from [EDB](https://www.enterprisedb.com/downloads/postgres-postgresql-downloads) (PostgreSQL 17) and
run it. Keep the default port `5432`, and set a password for the `postgres` superuser when asked (remember it). The installer
also installs the `psql` tool and a Windows Service that starts PostgreSQL at boot.

Now create a database and a user. Make a random password without `;` or `=` first:

```powershell
$b = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
$DbPass = ([Convert]::ToBase64String($b)) -replace '[^A-Za-z0-9]', ''
$DbPass        # keep it, it goes into the connection string in step 5
```

```powershell
& "C:\Program Files\PostgreSQL\17\bin\psql.exe" -U postgres -c "CREATE USER game WITH PASSWORD '$DbPass';"
& "C:\Program Files\PostgreSQL\17\bin\psql.exe" -U postgres -c "CREATE DATABASE game OWNER game;"
```

Each command asks for the `postgres` password from the installer. Tables are created by the server on its first start.

## 3. Get the code and build

```powershell
git clone https://github.com/AliSafi-GD/MWBS_GameServer.git
cd MWBS_GameServer

dotnet publish src/GameServer.Host -c Release -o C:\GameServer
dotnet publish samples/HelloGame.Modules -c Release -o C:\GameServer\modules\HelloGame.Modules
```

The server finds modules in the folder set by `GameServer:ModulesPath` (environment variable `GameServer__ModulesPath`).
Each module is `modules\<Name>\<Name>.dll`, and the folder name must equal the DLL name. Use an absolute path: a Windows Service
does not start in your folder. For your own game publish your own project into `C:\GameServer\modules\<YourModule>`.

## 4. The settings

Settings are environment variables. Set them for the whole machine so the service sees them (administrator PowerShell,
in the same window as step 2 so `$DbPass` still exists):

```powershell
function New-RandomText($bytes) {
  $b = New-Object byte[] $bytes
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  [Convert]::ToBase64String($b)
}
function Set-MachineEnv($name, $value) { [Environment]::SetEnvironmentVariable($name, $value, 'Machine') }

Set-MachineEnv 'ASPNETCORE_ENVIRONMENT' 'Production'
Set-MachineEnv 'ASPNETCORE_URLS' 'http://0.0.0.0:7350'
Set-MachineEnv 'GameServer__ModulesPath' 'C:\GameServer\modules'
Set-MachineEnv 'GameServer__Jwt__Secret' (New-RandomText 48)
Set-MachineEnv 'GameServer__Admin__Key' (New-RandomText 36)
Set-MachineEnv 'ConnectionStrings__Default' "Host=localhost;Database=game;Username=game;Password=$DbPass"
```

| Setting | Meaning |
|---|---|
| `ASPNETCORE_ENVIRONMENT=Production` | Never `Development` on a real server: it turns on public dev keys |
| `ASPNETCORE_URLS` | Where to listen. Without it the server only answers on `localhost` |
| `GameServer__Jwt__Secret` | Required, at least 32 characters |
| `GameServer__Admin__Key` | At least 24 characters, or leave it out to disable the admin panel |
| `ConnectionStrings__Default` | Required. The database from step 2 |

To read the admin key later: `[Environment]::GetEnvironmentVariable('GameServer__Admin__Key', 'Machine')`.
More settings are in [Settings and environment](../reference/settings); set them the same way.

## 5. First run in the foreground

Open a **new** administrator PowerShell (so it sees the new variables) and try it by hand. If something is wrong you see the
message right away:

```powershell
cd C:\GameServer
dotnet .\GameServer.Host.dll
```

You should see `Loaded module ...` lines and `Now listening on: http://0.0.0.0:7350`. In another window:

```powershell
Invoke-RestMethod http://localhost:7350/healthz
```

Stop it with Ctrl+C.

## 6. Run it as a Windows Service

```powershell
New-Service -Name GameServer -DisplayName "GameServer" -BinaryPathName "C:\GameServer\GameServer.Host.exe" -StartupType Automatic
Start-Service GameServer
Get-Service GameServer
```

The classic tool works too: `sc.exe create GameServer binPath= "C:\GameServer\GameServer.Host.exe" start= auto`
(the space after `binPath=` and `start=` is required).

The server knows when it runs as a service, so Windows can start and stop it normally. It starts at every boot.
To make Windows restart it after a crash:

```powershell
sc.exe failure GameServer reset= 86400 actions= restart/5000/restart/5000/restart/5000
```

## 7. Open the port

```powershell
New-NetFirewallRule -DisplayName "GameServer HTTP" -Direction Inbound -Protocol TCP -LocalPort 7350 -Action Allow
```

Add a rule for `7351` (TCP) if you use the TCP chat transport, and for `7360` (UDP) for UDP matches. On a cloud VM
(Azure, AWS...) open the port in the provider's network rules too.

## 8. Check that it works

`Invoke-RestMethod http://localhost:7350/healthz` should print `status ok` and the RPCs of your module. The admin panel is
`http://localhost:7350/admin` (sign in with `GameServer__Admin__Key`).

Then write the game config once: open `http://localhost:7350/arena/`, press **Write game config** and enter the admin key.
See [Tank Arena](../guides/tank-arena).

## Logs

A service has no console. Its log goes to the **Windows Event Log**: open *Event Viewer*, then *Windows Logs > Application*,
and look for the source `GameServer.Host`. In PowerShell:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'GameServer.Host' } -MaxEvents 30 |
  Format-List TimeCreated, Message
```

If the service does not start at all, run step 5 in the foreground: you see the same error on the screen.

## Everyday commands

| What | Command |
|---|---|
| Status | `Get-Service GameServer` |
| Restart / stop / start | `Restart-Service GameServer` (or `Stop-Service`, `Start-Service`) |
| Update the server | `git pull`, `Stop-Service GameServer`, publish again (step 3), `Start-Service GameServer` |
| Update your module | publish it into `C:\GameServer\modules\<Name>`, then `Restart-Service GameServer` |
| Change a setting | `Set-MachineEnv ...` again, then `Restart-Service GameServer` |
| Remove the service | `Stop-Service GameServer; sc.exe delete GameServer` |

## Next

- HTTPS and a domain name: [Put it on a VPS](./vps#https)
- The full checklist: [Deploy to production](../server/production)

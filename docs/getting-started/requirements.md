# Requirements

## For the server

| Tool | Version | Why |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0 | Builds and runs the host and your module |
| [Docker](https://www.docker.com/) | any recent | Runs PostgreSQL (and optionally the whole server) |
| PostgreSQL | 17 | The database. The Compose file starts it for you |
| Git | any | To download the project |

Check what you have:

```bash
dotnet --version     # 10.x
docker --version
git --version
```

## For the Unity client

| Tool | Version |
|---|---|
| Unity | 2021.3 or newer for the SDK package. The sample game uses **Unity 6 (6000.6.0f1)** |
| `com.unity.nuget.newtonsoft-json` | 3.2.1, installed automatically as a dependency |
| LiteNetLib (optional) | 1.3.x, only if you want UDP matches |

## For Android builds (optional)

Unity's Android Build Support (SDK, NDK, OpenJDK). See [Build an Android APK](../guides/android).

## Operating systems

Windows, Linux and macOS all work. The commands in these docs are for a Bash-like shell (Git Bash on Windows is fine).
`run-tests.ps1` is the Windows test runner and `run-tests.sh` is the Linux/macOS one.

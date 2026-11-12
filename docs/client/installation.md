# Installation

The Unity client is the package **`com.gameserver.client`** (Unity 2021.3+, C# 9). It uses `Newtonsoft.Json` and
`async/await` with `Task`.

## Add the package

1. Open your project. **Window > Package Manager > + > Add package from disk...**
2. Pick `sdk/unity/com.gameserver.client/package.json` from the downloaded repository.

Or edit `Packages/manifest.json` yourself:

```json
{
  "dependencies": {
    "com.gameserver.client": "file:../path/to/MWBS_GameServer/sdk/unity/com.gameserver.client"
  }
}
```

The package depends on `com.unity.nuget.newtonsoft-json` (3.2.1). Unity installs it automatically.

::: tip Keep the package outside the project
Use a relative `file:` path to the repository so the server and SDK versions stay together when you `git pull`.
:::

## Import the sample

Package Manager > **GameServer Client** > Samples > **QuickStart**. It signs in, joins a chat room and plays a match
(see [Quick start](./quick-start)).

## Optional: UDP with LiteNetLib

For fast action games you can use UDP matches.

1. Put `LiteNetLib.dll` (1.3.x) in any `Assets/Plugins` folder.
2. Add the scripting define `GAMESERVER_LITENETLIB` (Project Settings > Player > Scripting Define Symbols).
3. Start the server with `GameServer__Match__Transports=websocket,udp`.
4. In code: `options.UseLiteNetLib();` (namespace `GameServer.Client.Udp`).

Without the define the UDP assembly is not compiled, so nothing else breaks.

## Server address

`BaseUrl` is `http://localhost:5000` in the editor. On a phone use your computer's LAN address, and `https://` in production.

# Build an Android APK

The Tank Arena project has a build script: menu **Game > Build Android APK**. It builds with IL2CPP for ARM64, allows plain
`http`, and signs the APK with the debug key.

## Before you build

1. The phone must reach the server on your LAN.
2. Start the server so it listens on all interfaces, not only localhost:

   ```bash
   ASPNETCORE_URLS=http://0.0.0.0:5000 dotnet run --project src/GameServer.Host
   ```

3. Open an **inbound firewall rule** for that port on the computer.
4. Put the computer's LAN address into the build. The build script writes
   `Assets/Resources/server-url.txt` (git-ignored) to bake the server address into the APK.

## Notes

- *Optimized Frame Pacing* is turned off in the build script because Gradle could not reach Google's Maven from the author's network.
- **Behind a local proxy**: Gradle (Java) ignores `HTTPS_PROXY`, so downloads from Maven Central fail with a TLS handshake
  error. Start Unity with
  `JAVA_TOOL_OPTIONS="-Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=<port> -Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort=<port>"`.
- For a public server use `https://` and a real signing key.

::: info Not tested on a real phone yet
The APK has been built and driven in tests, but a real-device test is still on the to-do list.
:::

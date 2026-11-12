# Platforms and transports

## HTTP

`UnityWebRequestHttp` is used inside Unity and `HttpClientHttp` elsewhere (plain .NET tests). Swap with `options.Http`.

## Realtime

`options.RealtimeTransport = "websocket"` (default) or `"tcp"`. Add your own through `IRealtimeTransport`.

## Matches

WebSocket by default. UDP through `options.UseLiteNetLib()` ([Installation](./installation#optional-udp-with-litenetlib)).
Add your own through `IMatchTransportClient`.

## WebGL

Raw TCP and UDP are not available in browsers. **Use WebSocket.** The default .NET `ClientWebSocket` may not work in WebGL
builds; a JavaScript-based transport would need to be plugged in via `IRealtimeTransport`.

## Android

- Use your computer's LAN address as the server address, or `https://` for a public server.
- Plain `http` must be allowed in Player Settings for LAN testing (the sample build script does this).
- The server must listen on `0.0.0.0` and the firewall must allow its port.

See [Build an Android APK](../guides/android).

## Testing outside Unity

`sdk/unity/SdkSmokeTest` compiles the SDK sources as a plain .NET app and exercises every call against a live server.
`run-tests.ps1` / `run-tests.sh` build and run it; the log is `logs/sdk-test.log`. It is also a good source of correct usage.

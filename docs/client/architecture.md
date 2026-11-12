# Architecture tips

A layout that works well, taken from the Tank Arena client.

1. **A `Backend` singleton** ([Quick start](./quick-start)). One "Boot" scene: version check, config, login, wallet, then load "Menu".
2. **Thin services per area** (`WalletService`, `ShopService`, `ChatService`, `PlayService`) that call the SDK and expose C#
   events. **The UI never calls the SDK directly.**
3. **Game scene**: a service owns the `MatchConnection`; decode server snapshots into a state object; render from it
   (interpolate between snapshots); send only input.
4. **Loading and error states** for every awaited call. Disable buttons while a call is in flight.
5. **Save nothing authoritative locally.** The only thing stored on the device is the session (`PlayerPrefsStorage`).
   Things like "which chat messages were read" are fine to keep in PlayerPrefs.

## Tank Arena folders

```
Assets/Scripts/
  Core/    services and session, no UI            (Core/Services/*.cs)
  Meta/    boot, menu pages, popups, UI kit       (Boot, Menu, Ui)
  Arena/   the match: screen, state, touch sticks
  Editor/  scene and Android build scripts
  Testing/ headless bot
Assets/Tests/PlayMode/GameTours.cs   PlayMode tests that tour the whole UI and take screenshots
```

## Before you say "done"

- Compile with no warnings in Unity.
- Test with the server running (`run-tests.ps1`, or Docker plus `dotnet run`).
- Test two clients at once for any multiplayer feature.
- Kill the server mid-match, turn off the network, relaunch the app (session restore). Each must end in a clear UI state.

## For AI coding agents

`sdk/unity/AGENTS.md` is a compact version of the SDK guide written for coding agents: ground rules, bootstrap, cheat-sheet,
match flow, error codes. Point your agent at it before it writes code.

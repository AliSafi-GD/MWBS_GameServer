# Project notes (state of the work, for picking it up on another machine)

Read `README.md` for the server and `sdk/unity/AGENTS.md` for the rules of the Unity client. This file is only the
current state and what is left.

## Layout

- `src/` game server (accounts, wallet, shop, quests, social, chat, matchmaker, matches). `samples/HelloGame.Modules` is the
  server module with the Arena match handler.
- `sdk/unity/` Unity client package `com.gameserver.client` (+ `SdkSmokeTest`).
- `samples/unity-client/` the Unity 6 (6000.6.0f1) 2D game "Tank Arena" that uses the SDK. Scripts are in
  `Assets/Scripts`: `Core` (services, no UI), `Meta` (menu, boot, popups, UI kit), `Arena` (the match), `Editor`
  (scene and Android build scripts), `Testing` (headless bot). PlayMode tests: `Assets/Tests/PlayMode/GameTours.cs`.

## Set up on a new machine

1. `.env` from `.env.example`; Postgres with Docker (see README).
2. `dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules` (`modules/` is not in git).
3. Run the host, open `/arena/` once and press "Write game config" (shop, quests, match modes live in the DB, not in git).
4. Open `samples/unity-client` with Unity 6000.6.0f1. In Boot, the gear (bottom right) sets the server address.

## What works (all verified with screenshots and bots, not on a real phone)

- Boot (version check, maintenance, force/soft update popups, sign-in, loading), menu with five pages: Shop (+ Inventory
  with Equip for tank skins), Quests (+ daily reward, achievements), Battle, Social (friends, party, lobby chat),
  Ranking. Profile (avatar, name, username, delete account) opens from the top-left avatar.
- Arena: matchmaker, private rooms, 2D match, skins, touch sticks, reconnect (written, not tested).
- Tests: `GameTours` (MenuTour, PlayOneMatch, PopupTour, ProfileTour, InventoryTour, ChatTour), `ArenaBots.Run`,
  `GameBuild.SmokeBoot`. Screenshots go to `logs/shots` (the path is hard-coded in `GameTours.cs`: change it).

## Not done / next

- Chat with a friend (DM), party chat, clan chat, and a whole clan page (create, search, join, members). The SDK and server
  support all of it (`Social.Groups`, channels `dm:`, `party:`, `group:`); the UI only has the lobby room.
- Does the server keep lobby messages after a restart? `Chat.HistoryAsync` returned 0 once. Not checked.
- Unread badge for chat; own-tank movement prediction; UDP match transport; real-device test of the APK.
- Only skins exist as items on the server. A non-skin item card exists but has never been shown.

## Gotchas

- The repo is public. `appsettings.Development.json` and the Postman files hold dev-only keys: never use them in production.
- `Ui.Pin` centers a rect on its anchor unless the anchor is exactly 0 or 1, then it hugs that edge.
- Unity cannot open the same project twice. If the editor has it open, batch runs must use a copy of the project.
- Android: menu "Game > Build Android APK" (IL2CPP, ARM64, plain http allowed, Optimized Frame Pacing off because Gradle could
  not reach Google's Maven from this network). The phone needs the server on the LAN, the server listening on `0.0.0.0` and an
  inbound firewall rule for its port. The APK is signed with the debug key.
- Tests use the guest account saved in the editor's PlayerPrefs; it was renamed "Tank Hero" and owns `skin_red`.
- `Assets/Resources/server-url.txt` (git-ignored) is written by the Android build to bake in the server address.

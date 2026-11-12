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

1. `.env` from `.env.example`; Postgres with Docker (see README). If port 5432 is taken (a local PostgreSQL service), run the
   container on another port and pass it to the host:
   `docker run -d --name mwbs-pg -e POSTGRES_DB=game -e POSTGRES_USER=game -e POSTGRES_PASSWORD=change-me -p 127.0.0.1:5433:5432 -v mwbs_pgdata:/var/lib/postgresql/data postgres:17`,
   then start the host with `ConnectionStrings__Default="Host=localhost;Port=5433;Database=game;Username=game;Password=change-me"`.
2. `dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules` (`modules/` is not in git).
3. Run the host, open `/arena/` once and press "Write game config" (shop, quests, match modes live in the DB, not in git).
4. Open `samples/unity-client` with Unity 6000.6.0f1. In Boot, the gear (bottom right) sets the server address.

## What works (all verified with screenshots and bots, not on a real phone)

- Boot (version check, maintenance, force/soft update popups, sign-in, loading), menu with five pages: Shop (+ Inventory
  with Equip for tank skins), Quests (+ daily reward, achievements), Battle, Social (friends, party, chat, clan),
  Ranking. Profile (avatar, name, username, delete account) opens from the top-left avatar.
- Arena: matchmaker, private rooms, 2D match, skins, touch sticks, reconnect (written, not tested).
- Chat (`Core/Services/ChatService.cs`, one `ChatThread` per conversation): lobby, direct messages with friends, party chat,
  clan chat. Unread counts on the conversation list, the chat buttons and the Social tab, plus a toast for a message that is
  not on screen. No server change was needed. What was read is kept in PlayerPrefs on the device (the server stores messages,
  not read state), and to find messages sent while offline the client loads the last 40 messages of up to 30 friends once per
  session; a "my conversations with unread counts" endpoint on the server would replace both.
- Clan tab: create, search, join / request, accept an invite, member list, leave, disband (owner). Not there: invite, kick,
  promote, transfer, accepting join requests, editing the clan (all in `Social.Groups` of the SDK).
- Tests: `GameTours` (MenuTour, PlayOneMatch, PopupTour, ProfileTour, InventoryTour, ChatTour, ChatThreadsTour),
  `ArenaBots.Run`, `GameBuild.SmokeBoot`. Screenshots go to `logs/shots` at the repository root.

## Not done / next

- Clan management (see above). Chat: blocking a player, loading older messages than the last 40.
- Lobby messages are not kept: the server only stores rooms with `GameServer:Chat:PersistRooms` = true (default false), which
  is why `Chat.HistoryAsync` returns 0 for the lobby. Direct and clan messages are always stored, party chat never.
- Own-tank movement prediction; UDP match transport; real-device test of the APK.
- Only skins exist as items on the server. A non-skin item card exists but has never been shown.

## Gotchas

- The repo is public. `appsettings.Development.json` and the Postman files hold dev-only keys: never use them in production.
- `Ui.Pin` centers a rect on its anchor unless the anchor is exactly 0 or 1, then it hugs that edge.
- Unity cannot open the same project twice. If the editor has it open, batch runs must use a copy of the project.
  Keep the copy on a short path: under a deep temp folder the package import fails (paths over 260 characters) and Boot shows
  "The referenced script (Unknown) on this Behaviour is missing". The copy needs `sdk/unity` three folders up, like the repo.
- Android: menu "Game > Build Android APK" (IL2CPP, ARM64, plain http allowed, Optimized Frame Pacing off because Gradle could
  not reach Google's Maven from this network). The phone needs the server on the LAN, the server listening on `0.0.0.0` and an
  inbound firewall rule for its port. The APK is signed with the debug key.
- Android build behind a local proxy: Gradle (Java) ignores `HTTPS_PROXY`, so downloads from Maven Central fail with a TLS
  handshake error. Start Unity with
  `JAVA_TOOL_OPTIONS="-Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=<port> -Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort=<port>"`.
- Tests use the guest account saved in the editor's PlayerPrefs; it was renamed "Tank Hero" and owns `skin_red`.
- `Assets/Resources/server-url.txt` (git-ignored) is written by the Android build to bake in the server address.

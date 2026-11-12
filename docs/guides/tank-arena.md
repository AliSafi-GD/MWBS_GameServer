# Tank Arena demo game

Tank Arena is a 1v1 tank game that uses almost everything in the server. It exists in two forms:

| Form | Where | Purpose |
|---|---|---|
| Browser | <http://localhost:5000/arena/> (second player: `/arena/?p=2` in another tab) | Quick check of the server |
| Unity 6 | `samples/unity-client` | The reference for building a real game with the SDK |

Both talk to the same server logic: `samples/HelloGame.Modules/Arena/ArenaMatch.cs`.

## Set up

1. Run the server ([Run the server](../getting-started/run-the-server)), with the module published.
2. Open <http://localhost:5000/arena/>, and on the login page press **Write game config** once (needs the admin key).
   It reads `src/GameServer.Host/wwwroot/arena/arena-config.json` and saves the match modes (`arena`, `arena_friendly`), shop,
   quests, achievements, leaderboards and a tournament **into the database**.
3. Open `samples/unity-client` with Unity **6000.6.0f1** and press Play in the Boot scene. The gear icon sets the server address.

## What the Unity client contains

- **Boot**: version check, maintenance, force and soft update popups, sign-in, loading.
- **Menu with five pages**: Shop (plus Inventory with Equip for tank skins), Quests (plus daily reward and achievements),
  Battle, Social (friends, party, chat, clan), Ranking. Profile opens from the avatar.
- **Arena**: matchmaker, private rooms, 2D match, skins, touch sticks, reconnect.
- **Chat**: lobby, direct, party and clan chat with unread counts and toasts.
- **Clan tab**: create, search, join or request, accept an invite, members, leave, disband.

## Scripts map

```
Assets/Scripts/Core/            GameSession.cs, Services/ (Chat, Economy, Social, Play, Profile, StartupCheck)
Assets/Scripts/Meta/            Boot/BootScreen.cs, Menu/* pages, Ui/* UI kit and popups
Assets/Scripts/Arena/           ArenaScreen.cs, ArenaState.cs, StickPad.cs
Assets/Scripts/Editor/          scene builders, AndroidBuild.cs, GameBuild.cs, ArenaBots.cs
Assets/Scripts/Testing/         ArenaBot.cs (headless bot)
Assets/Tests/PlayMode/          GameTours.cs
```

## How the match works

The server decides everything. Clients only send input:

```
client -> server   {"t":"hello","skin":"skin_gold"}
                   {"t":"in","dx":-1..1,"dy":-1..1,"aim":radians,"fire":true,"seq":n}
                   {"t":"chat","text":"..."}
server -> client   init, players, s (snapshot, 20 per second), hit, left, chat, over, warn
```

The handler moves tanks and bullets, decides hits and the winner, pays coins, records stats and leaderboards. A dropped player
can rejoin within 15 seconds with `POST /v1/matches/{id}/join`. Snapshots are sent as `Sequenced`, so they become real
UDP-style messages on a UDP transport.

## Bots

Load-test or smoke-test the match with bots:

```bash
dotnet run --project tests/GameServer.ArenaBots -- http://localhost:5000 <adminKey> 8
```

It creates 8 players, plays four full matches through the matchmaker and checks coins, stats, quests, achievements,
leaderboards and the shop afterwards (`arena-bots.log`). Raise the number to load-test (up to 200).

## Known gaps

Clan management beyond create/join/leave, blocking in chat, older chat history than 40 messages, own-tank movement prediction,
UDP match transport in the Unity client, and a real-device test of the APK.

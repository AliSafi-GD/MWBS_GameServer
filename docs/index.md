---
layout: home

hero:
  name: GameServer
  text: A game backend you host yourself
  tagline: Accounts, wallet, shop, quests, leaderboards, friends, clans, chat, matchmaking and server-side matches. One C# server per game, one Unity SDK.
  actions:
    - theme: brand
      text: Get started
      link: /getting-started/introduction
    - theme: alt
      text: Unity client
      link: /client/installation
    - theme: alt
      text: GitHub
      link: https://github.com/AliSafi-GD/MWBS_GameServer

features:
  - title: Everything a live game needs
    details: Guest and provider login, refresh tokens, remote config, version check, maintenance mode, wallet, shop, quests, achievements, daily rewards, leaderboards, tournaments and an inbox.
  - title: Server-authoritative
    details: Clients send intent. Coins, scores, wins and items are decided on the server, inside your own module (RPCs, hooks and match handlers written in C#).
  - title: Realtime and matches
    details: Chat, parties and notifications over WebSocket or TCP. Matches over WebSocket or UDP, chosen per game mode. Add your own protocol with a small interface.
  - title: Unity SDK
    details: One package, async/await, events on the main thread. Includes a complete sample game (Tank Arena) with a shop, quests, social, chat and an online 2D match.
  - title: Admin panel
    details: Search and ban players, edit config with history and rollback, run live-ops broadcasts, watch a dashboard, test RPCs.
  - title: Runs anywhere
    details: ASP.NET Core and PostgreSQL. Start with `dotnet run`, or use Docker Compose.
---

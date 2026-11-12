# Introduction

GameServer is a small, self-hosted game backend written in C# (ASP.NET Core + PostgreSQL). It is built in the spirit of
Nakama: you run **one server per game**, and the game-specific logic lives in a **module**, a DLL that the server loads at start.

## What you get

| Area | What it does |
|---|---|
| Accounts | Guest login by device id, Google Play login, phone (OTP) login, account linking, refresh tokens |
| Remote config | Free-form JSON per key, public or server-only, with ETag. Version check and maintenance mode |
| Wallet and inventory | Currencies and stackable items, atomic transactions, permanent ledger |
| Shop | Products priced in wallet currencies and items, purchase limits, timed offers |
| Progress | Stats, achievements, quests (daily, weekly, monthly), login-streak daily reward |
| Competition | Leaderboards (best, set, incr; daily to never reset) and tournaments with entry fee and rewards |
| Inbox | Player notifications with an optional claimable reward, live events |
| Social | Friends, clans (groups), parties |
| Chat and realtime | Rooms, direct, group and party channels over WebSocket or TCP |
| Matches | Matchmaker and server-side match handlers over WebSocket or UDP |
| Admin | Web panel and API: players, config history, live-ops, dashboard, audit log, RPC tester |
| Unity SDK | The `com.gameserver.client` package, plus the Tank Arena sample game |

## How the pieces fit

```
Unity game  ──REST──────────────┐
(SDK)       ──WebSocket / TCP───┼──►  GameServer.Host  ──►  PostgreSQL
            ──WebSocket / UDP───┘          │
                                           └─ loads  modules/<Name>/<Name>.dll   (your game logic)
```

The repository is laid out like this:

```
src/GameServer.Abstractions   interfaces a module compiles against (the contract; only ever grows)
src/GameServer.Core           module loader, auth (JWT), accounts, storage, Postgres + migrations
src/GameServer.Host           the ASP.NET Core executable, admin panel, web playground
src/GameServer.Transport.*    WebSocket, TCP and UDP (LiteNetLib) transports, each a separate plugin
samples/HelloGame.Modules     example server module (RPCs, hooks, the Arena match)
sdk/unity/                    Unity client package com.gameserver.client
samples/unity-client/         "Tank Arena", a Unity 6 game built on the SDK
tests/                        smoke test and Arena bots
```

## The one rule

> **The server is the authority.** A client never decides coins, XP, wins, scores or inventory.
> It sends *intent* ("buy product X", "claim quest Y", "my stick is pointing left") and the server validates and answers.

Everything in these docs follows from that rule: balances can only be changed by server code, scores are submitted by
modules and match handlers, and shop prices are always read from the server config.

## Where to go next

1. [Requirements](./requirements) and [Download the project](./download)
2. [Run the server](./run-the-server) (or [with Docker](./docker))
3. [Your first request](./first-request) with curl
4. [Your first Unity game](./first-unity-game)

# Admin panel

Open <http://localhost:5000/admin> and sign in with the admin key (in Development:
`dev-admin-key-do-not-use-in-production-0123456789`).

Set `GameServer__Admin__Key` (min 24 characters) to enable it elsewhere. Leave it empty to **disable** the panel and the API.
The panel talks to `/admin/api/*`, which requires the `X-Admin-Key` header.

## Tabs

| Tab | What you can do |
|---|---|
| **Users** | Search players, edit the profile, ban/unban, export their data (privacy), delete accounts. Detail: balances, ledger, storage keys, friends, groups, mute, timeline |
| **Dashboard** | Online players, live matches, matchmaking queue, new/active players, 24 h economy per currency, charts for online players and matches. Refreshes every 10 s. A sample is saved every minute and kept 7 days |
| **Config** | Edit any config key. Every change keeps the previous value (last 50 per key) with rollback. Templates for leaderboards, tournaments, shop... |
| **Shop**, **Progress**, **Leaderboards** | View catalogs and progress, remove a cheater from a board |
| **Groups**, **Chat**, **Online**, **Social rules** | Moderate clans and chat, mute, disconnect |
| **Matches** | Running matches, end one |
| **Live-Ops** | Scheduled inbox messages with an optional reward (to all players or only those online at send time) and a tournament editor |
| **Modules** | RPCs, match handlers and transports that the loaded modules registered, and an **RPC tester** |
| **Admins**, **Audit log** | Admin role only: create admins, see who did what |

::: warning The RPC tester changes real data
It runs an RPC as a chosen player, hooks included.
:::

## Roles

The key from settings is always `root` with the **Admin** role. Create more admins with
`POST /admin/api/admins {name, role}`. The key (`gsk_...`) is shown **once** and only its hash is stored;
`POST /admin/api/admins/{id}/reset-key` replaces it.

| Role | Can |
|---|---|
| **Admin** | Everything |
| **Support** | Read everything; ban/unban, mute, disconnect, send messages to a player, delete chat messages, kick/ban group members |
| **ReadOnly** | Read, except admins and audit |

Denied calls answer `403 insufficient_role`.

## Audit log

Every write is saved: who, role, method, path, the start of the body (secrets hidden), status and IP. Kept 180 days.
Denied attempts are logged too. `GET /admin/api/audit?actor=&q=&before=&limit=`.

## Live-Ops

```
POST /admin/api/liveops/broadcasts
{ "runAtUtc": "...", "subject": "...", "body": "...", "reward": {...}, "audience": "all" | "online", "expiresInDays": 7 }
```

Checked about every 15 seconds. `GET` lists, `DELETE` cancels.

## Security

In production serve it over HTTPS and, ideally, restrict `/admin` to your own IP or VPN.
See [Deploy to production](./production) and the [Admin API](../reference/admin-api) reference.

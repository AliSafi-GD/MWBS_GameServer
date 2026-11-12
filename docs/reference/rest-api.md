# REST API

All player endpoints are under `/v1`. Send `Authorization: Bearer <token>` unless the table says "none".
Bodies are JSON. Errors are `{ "error": "<code>", "message": "<text>" }`.

::: tip Try them live
Open `/playground/` on your server for every endpoint with a Send button, or import `postman/GameServer.postman_collection.json`.
:::

## Account and auth

| Method | Path | Auth | What |
|---|---|---|---|
| POST | `/v1/auth/device` | none | `{deviceId, username?, create?}` -> `token`, `refreshToken` |
| GET | `/v1/auth/providers` | none | Enabled providers |
| POST | `/v1/auth/{provider}/begin` | none | First step (phone sends a code) |
| POST | `/v1/auth/{provider}` | none | Login `{credential, create?, username?}` |
| POST | `/v1/auth/refresh` | none | `{refreshToken}` -> new pair |
| POST | `/v1/auth/logout` | | `{refreshToken}` ends one session |
| POST | `/v1/account/logout-all` | Bearer | End all sessions |
| GET / PUT / DELETE | `/v1/account` | Bearer | Profile |
| POST / DELETE | `/v1/account/link/{provider}` | Bearer | Link / unlink a login |
| GET | `/v1/users?ids=a,b,c` | Bearer | Public profiles (max 100) |

## Config and status

| Method | Path | Auth | What |
|---|---|---|---|
| GET | `/v1/config` | none | Public config keys, ETag |
| GET | `/v1/version-check?store=&version=` | none | `ok`, `update_available`, `update_required` |
| GET | `/v1/realtime` | | Enabled realtime transports |
| GET | `/healthz` | none | Status and loaded RPCs |

## Game

| Method | Path | What |
|---|---|---|
| GET | `/v1/wallet`, `/v1/inventory` | Balances, items |
| GET | `/v1/shop?category=` | Products |
| POST | `/v1/shop/{id}/buy` | `{quantity, requestId}` |
| GET | `/v1/stats` | Stats |
| GET | `/v1/achievements`, `/v1/quests` | Progress |
| POST | `/v1/achievements/{id}/claim`, `/v1/quests/{id}/claim` | Claim |
| GET | `/v1/daily` | Streak state |
| POST | `/v1/daily/claim` | Claim today |
| GET | `/v1/leaderboards/{id}?limit=&around=&period=` | Board |
| GET | `/v1/tournaments`, `/v1/tournaments/{id}` | Tournaments |
| POST | `/v1/tournaments/{id}/join`, `/claim` | Join, claim |
| GET | `/v1/notifications?unread=true` | Inbox |
| POST | `/v1/notifications/{id}/read`, `/claim` | |
| DELETE | `/v1/notifications/{id}` | |
| POST | `/v1/rpc/{id}` | Call a module RPC |

## Social

| Method | Path | What |
|---|---|---|
| GET / POST | `/v1/friends` | List (`?state=`), add or accept |
| DELETE | `/v1/friends/{userId}` | Remove |
| POST / DELETE | `/v1/friends/{userId}/block` | Block, unblock |
| GET / POST | `/v1/groups` | Search, create. Also `/mine`, `/{id}`, `/{id}/join`, `/leave`, `/members`, invite, accept, kick, ban, promote, demote, transfer |
| GET / POST | `/v1/parties` | `/mine`, join, leave, invite, kick, promote, close |
| GET | `/v1/chat/...` | Channel history |

## Matches

| Method | Path | What |
|---|---|---|
| GET | `/v1/match/modes` | Modes from the `matches` config |
| POST | `/v1/matchmaker/tickets` | `{mode, properties, partyId}` |
| DELETE | `/v1/matchmaker/tickets/{id}` | Cancel |
| POST | `/v1/matches` | Create (modes with `allowClientCreate`) |
| GET | `/v1/matches?mode=` | List |
| POST | `/v1/matches/{id}/join` | Reserve a seat, returns a token |

## Examples

```bash
curl -X POST http://localhost:5000/v1/auth/device -H "Content-Type: application/json" -d '{"deviceId":"my-test-device-0001"}'
curl http://localhost:5000/v1/wallet -H "Authorization: Bearer $TOKEN"
curl -X POST http://localhost:5000/v1/shop/gems_small/buy -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"quantity":1,"requestId":"9b2f7e0a-3c1d-4b8e-a1f4-6d2e5c7a9b10"}'
curl -X POST http://localhost:5000/v1/rpc/claim_daily_reward -H "Authorization: Bearer $TOKEN"
```

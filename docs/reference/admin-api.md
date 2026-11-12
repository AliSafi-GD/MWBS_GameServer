# Admin API

Base path `/admin/api`. Every call needs the header `X-Admin-Key: <key>` (the key from settings is the `root` admin, or a
`gsk_...` key of an admin you created). The API is off when `GameServer__Admin__Key` is empty. Admin calls are limited to
600 per minute per IP.

```bash
curl http://localhost:5000/admin/api/dashboard -H "X-Admin-Key: $ADMIN_KEY"
```

## Dashboard

- `GET /dashboard`, `GET /dashboard/series?minutes=`

## Users

- `GET /users?q=`, `GET/PUT/DELETE /users/{id}`, ban and unban, privacy export
- `GET/POST /users/{id}/economy`, `GET /users/{id}/ledger`
- `GET/PUT/DELETE /users/{id}/storage[/{key}]`
- `GET /users/{id}/timeline`, `/progress`, `/shop`, `/friends`, `/groups`, `/notifications`
- `POST /users/{id}/stats` `{stat, value, mode: add|max}`
- `POST /users/{id}/mute` `{minutes, reason}`, `DELETE /users/{id}/mute`, `POST /users/{id}/disconnect`
- `POST /users/{id}/notifications`

## Config

- `GET /config`, `PUT /config/{key}` `{value, public}`
- `GET /config/{key}/history`, `POST /config/{key}/rollback/{historyId}`

## Game data

- `GET /shop`, `GET /progress`, `GET /leaderboards/{id}` (`tournament:<id>`)
- `GET /groups?q=&type=`, `GET/PUT/DELETE /groups/{id}`, `POST /groups/{id}/members/{userId}/kick|ban`, `POST /groups/{id}/transfer/{userId}`
- `DELETE /users/{id}/friends/{otherId}`

## Chat and presence

- `GET /chat/channels`, `GET /chat/messages?channel=`, `DELETE /chat/messages/{id}`, `POST /chat/send`
- `GET /chat/mutes`, `GET /online`

## Matches and modules

- `GET /matches`, `DELETE /matches/{id}`
- `GET /modules`, `POST /rpc-test` `{userId, id, payload}` (changes real data)

## Live-ops

- `POST /notifications/broadcast`
- `GET/POST /liveops/broadcasts`, `DELETE /liveops/broadcasts/{id}`
- Tournament editor: writes the `tournaments` config key

## Admins and audit (Admin role)

- `GET/POST /admins`, `POST /admins/{id}/reset-key`
- `GET /audit?actor=&q=&before=&limit=`

Roles: **Admin** (everything), **Support** (read; ban, mute, disconnect, message, moderate chat and groups), **ReadOnly**.
Denied calls answer `403 insufficient_role` and are logged. See [Admin panel](../server/admin).

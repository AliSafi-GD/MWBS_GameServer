# Friends, clans and parties

## Friends

`/v1/friends`

| Method | Path | What |
|---|---|---|
| GET | `/v1/friends?state=` | List by state: `friend`, `invite_received`, `invite_sent`, `blocked` |
| POST | `/v1/friends` | Add, or accept a received invite |
| DELETE | `/v1/friends/{userId}` | Remove |
| POST / DELETE | `/v1/friends/{userId}/block` | Block / unblock |

## Clans (groups)

"Clan" and "group" are the same thing. The `type` field picks the limits from the config key `social`:

```json
{ "groupTypes": { "clan": { "maxPerUser": 1, "maxMembers": 50 } } }
```

`/v1/groups`: create, search, `/mine`, get / update / delete, `/{id}/join`, `/leave`, `/members`, invite, accept, kick, ban,
promote, demote, transfer.

Errors you will meet: `not_group_admin`, `group_full`, `banned_from_group`.

## Parties

A party is a temporary squad, used to queue for a match together.

`/v1/parties`: create, `/mine`, join, leave, invite, kick, promote, close.

::: warning In memory
Parties live in memory (one server instance only) and need an open realtime connection. They are not shared between several
server instances.
:::

Settings: `GameServer:Parties` (`MaxSize` 8, `GraceSeconds` 15, `InviteMinutes` 5, `RequireConnection` true).
Realtime events: `party.update`, `friend.request`, `group.invite`.

## Admin

Tabs **Groups**, **Chat**, **Online** and **Social rules**; the Users tab also shows each player's friends, groups and chat mute.

- `GET/PUT/DELETE /groups/{id}`, `GET /groups?q=&type=`, `POST /groups/{id}/members/{userId}/kick|ban`, `POST /groups/{id}/transfer/{userId}`
- `GET /users/{id}/friends`, `DELETE /users/{id}/friends/{otherId}`, `GET /users/{id}/groups`

(all under `/admin/api`, header `X-Admin-Key`)

## Hooks

`friends.*`, `groups.*`, `parties.*`. See [Hooks](./hooks).

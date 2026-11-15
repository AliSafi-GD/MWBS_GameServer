# Error codes

Every error has an `error` code and a human `message`. In the Unity SDK they are `ApiException.Code` and `.Message`.

## Account and auth

| Code | HTTP | Meaning |
|---|---|---|
| `invalid_credential` | 401 | Wrong or expired credential |
| `invalid_refresh_token` | 401 | Refresh token reused or unknown. The session ended. Log in again |
| `account_disabled` | 403 | Banned or deleted |
| `username_taken` | 409 | Username in use |
| `identity_in_use` | 409 | That login belongs to another account |
| `provider_already_linked` | 409 | This account already has that provider |
| `last_login_method` | 403 | Cannot unlink the only login |
| `sms_not_configured` | 503 | Phone login without an `ISmsSender` |
| `account_not_found` | 404 | Login with `create: false` and no such account |
| `invalid_device_id` | 400 | `deviceId` is not 16-128 characters |
| `unknown_provider` | 404 | No such login provider, or it is switched off (`GameServer:Auth:Providers`), also for `device` |
| `not_supported` | 400 | The login method has no such step (`begin` on googleplay, `link/device`) |
| `invalid_code` | 401 | Wrong or expired phone code |
| `too_soon` | 429 | Phone code requested again within 60 seconds |
| `insufficient_role` | 403 | Admin role too low |
| `forbidden` | 403 | Wrong admin key |
| `admin_disabled` | 404 | The admin API is off (no `GameServer__Admin__Key`) |

## Config and platform

| Code | Meaning |
|---|---|
| `maintenance` | Server in maintenance, HTTP 503 (`message`, `until`) |
| `invalid_version` | `version-check` was called with a version that is not like `1.2` or `1.2.3` (400) |
| `rpc_not_found` | No module registered that RPC id (404) |
| 429 | Rate limited. Back off and retry |

A client that is too old is not an error: `GET /v1/version-check` answers `status: "update_required"`.

## Leaderboards, tournaments and notifications

| Code | HTTP | Meaning |
|---|---|---|
| `unknown_leaderboard`, `unknown_tournament` | 404 | Not defined in config |
| `tournament_not_active` | 409 | Join or submit outside the tournament's time window |
| `tournament_not_ended` | 409 | Claim before the end |
| `not_joined` | 403 | Claim or submit without joining |
| `no_score`, `no_reward` | 409 | Nothing to claim |
| `already_claimed` | 409 | Reward already paid |
| `reward_unclaimed` | 409 | Delete a notification before claiming its reward |
| `expired` | 409 | The notification has expired |
| `invalid_period` | 400 | `period` is not `current`, `all`, a day, a week or a month |

## Economy and shop

| Code | Meaning |
|---|---|
| `insufficient_funds`, `insufficient_items` | Not enough (409) |
| `invalid_transaction` | Bad request (400) |
| `unknown_currency`, `unknown_item`, `limit_reached` | Catalog rules |
| `product_not_found` | 404 |
| `product_unavailable`, `purchase_limit_reached` | 409 |

## Progress

| Code | Meaning |
|---|---|
| `goal_not_complete` | Claim before the target (409) |
| `already_claimed` | Reward already paid (409) |
| `daily_already_claimed` | Daily reward taken (409) |

## Social and chat

| Code | Meaning |
|---|---|
| `not_friends`, `friend_limit` | |
| `already_in_party`, `party_full`, `not_party_leader` | |
| `not_group_admin`, `group_full`, `banned_from_group` | |
| `muted` | The player is muted |
| `not_a_member` | Not in that clan or party |
| `not_stored` | `POST /v1/chat/read` on party chat or a room (only direct and clan chats keep a read mark) |

## Matches

| Code | Meaning |
|---|---|
| `already_queued` | Already has a ticket for the mode |
| `matchmaking_disabled` | Mode has `matchmaking: false` |
| `match_not_found`, `match_full` | |
| `match_create_forbidden` | Mode lacks `allowClientCreate` |
| `kicked_from_match` | Removed by the handler |

## Your own codes

Throw `new GameException("my_code", "Text")` in a module. Document your codes for your client team.

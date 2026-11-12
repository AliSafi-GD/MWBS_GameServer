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
| `insufficient_role` | 403 | Admin role too low |

## Config and platform

| Code | Meaning |
|---|---|
| `maintenance` | Server in maintenance (`message`, `until`) |
| `app_version` | Client too old |
| 429 | Rate limited. Back off and retry |

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

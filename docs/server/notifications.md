# Notifications

A per-player inbox. A message can carry a reward that the player claims; it is paid once through the wallet, with an
idempotency key.

## Sending

From a module:

```csharp
await ctx.Notifications.SendAsync(userId, "Subject", "Body",
    new NotificationReward { Wallet = new() { ["coins"] = 50 } }, expiresUtc);
```

Rewards are checked against the [economy catalog](./economy) when sent, so an unpayable reward is rejected early.

From the admin panel (user detail): send to one player or to everyone. API:
`POST /admin/api/users/{id}/notifications`, `POST /admin/api/notifications/broadcast`,
`GET /admin/api/users/{id}/notifications` (includes expired ones). The body is
`{ subject, body, reward?, expiresInDays? }`. Scheduled sends are in **Live-Ops**.

## Reading (client)

| Call | What |
|---|---|
| `GET /v1/notifications?limit=50&unread=true` | List the inbox |
| `POST /v1/notifications/{id}/read` | Mark read (204, or 404 `not_found`) |
| `POST /v1/notifications/{id}/claim` | Claim the reward. Answers `{ reward, wallet, items }` with the new balances |
| `DELETE /v1/notifications/{id}` | Delete (204). An unclaimed reward must be claimed first: 409 `reward_unclaimed` |

Expired messages are hidden and cannot be claimed (409 `expired`). Claiming twice is 409 `already_claimed`; a message
without a reward is 409 `no_reward`.

## Live event

When a notification is saved, online players receive the realtime event `notification.new`
`{ id, broadcast, subject, hasReward }` (`id` is null for broadcasts). The client then reads the inbox.
Offline players read it at the next login. In the SDK this is `Realtime.NotificationReceived`.

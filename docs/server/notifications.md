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
`GET /admin/api/users/{id}/notifications`. Scheduled sends are in **Live-Ops**.

## Reading (client)

| Call | What |
|---|---|
| `GET /v1/notifications?unread=true` | List the inbox |
| `POST /v1/notifications/{id}/read` | Mark read |
| `POST /v1/notifications/{id}/claim` | Claim the reward |
| `DELETE /v1/notifications/{id}` | Delete (an unclaimed reward must be claimed first) |

Expired messages are hidden and cannot be claimed.

## Live event

When a notification is saved, online players receive the realtime event `notification.new`
`{ id, broadcast, subject, hasReward }` (`id` is null for broadcasts). The client then reads the inbox.
Offline players read it at the next login. In the SDK this is `Realtime.NotificationReceived`.

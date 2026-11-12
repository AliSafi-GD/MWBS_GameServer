# Wallet, shop and progress

## Wallet and inventory

```csharp
Dictionary<string, long> wallet    = await client.Economy.WalletAsync();      // currency -> amount
Dictionary<string, long> inventory = await client.Economy.InventoryAsync();   // item -> count
```

The client can only **read**. Balances change through the shop, quests, achievements and your own server code.

## Shop

```csharp
List<ShopProduct> products = await client.Shop.ListAsync(category: "gems");   // price, limits, availability, remaining

var purchase = await client.Shop.BuyAsync("gems_small", quantity: 1, requestId: Guid.NewGuid().ToString());
```

Use a **fresh `requestId` per tap** and reuse it when you retry after a lost response. Then the purchase happens once.
Disable the button while the call is in flight.

Errors: `insufficient_funds`, `product_unavailable`, `limit_reached`, `product_not_found`.

## Stats

```csharp
var stats = await client.Progress.StatsAsync();    // counters like matches_won (written by the server)
```

## Achievements and quests

```csharp
var achievements = await client.Progress.AchievementsAsync();
var quests       = await client.Progress.QuestsAsync();

await client.Progress.ClaimAchievementAsync("win_10");
await client.Progress.ClaimQuestAsync("play_3");
```

A `Goal` has progress, target and claimed. Claim only when complete (`goal_not_complete`, `already_claimed`).
Listen for `goal.completed` on the realtime connection to show a toast:

```csharp
client.Realtime.On("goal.completed", data => ShowToast(data["name"]?.ToString()));
```

## Daily reward

```csharp
var daily = await client.Progress.DailyAsync();       // streak, claimedToday, nextDay, nextClaimUtc
if (!daily.ClaimedToday) await client.Progress.ClaimDailyAsync();   // daily_already_claimed
```

## Notifications (inbox)

```csharp
var list = await client.Notifications.ListAsync(limit: 50, unreadOnly: true);
await client.Notifications.MarkReadAsync(id);
await client.Notifications.ClaimAsync(id);      // notification with a reward
await client.Notifications.DeleteAsync(id);     // claim the reward first
client.Realtime.NotificationReceived += n => RefreshInbox();
```

## Custom server functions

```csharp
string raw = await client.Rpc.CallAsync("hello");
var result = await client.Rpc.CallAsync<MyResult>("buy_card", new { card = "card_fire" });
```

See [RPC functions](../server/rpc) for the server side.

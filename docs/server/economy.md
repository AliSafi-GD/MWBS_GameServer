# Wallet and inventory

Games define their own currency and item names (lowercase, e.g. `coins`, `card_fire`). The server enforces the rules:

- balances never go below 0
- a transaction is **all-or-nothing**
- every change is saved in the `ledger` (reason, amount, balance after)

**Only server code changes balances.** Clients can read them: `GET /v1/wallet`, `GET /v1/inventory`.

## Transactions in a module

```csharp
// pay 100 coins and receive a card, together or not at all
var tx = new EconomyTransaction("buy_card").Currency("coins", -100).Item("card_fire", 1);
var result = await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);
// throws insufficient_funds / insufficient_items
```

### Idempotency

Pass an idempotency key as the second constructor argument for operations that must happen once:

```csharp
new EconomyTransaction("daily_reward", "daily:20261108")
```

A repeat returns `result.AlreadyApplied == true` and changes nothing.

### Errors

`insufficient_funds` / `insufficient_items` (409), `invalid_transaction` (400), `unknown_currency`, `unknown_item`, `limit_reached`.

## Catalog (optional, recommended)

Save the config key `economy`:

```json
{ "currencies": { "coins": { "name": "Coins", "max": 999999 }, "gems": { "name": "Gems" } },
  "items":      { "card_fire": { "stack": 99 } } }
```

With the key present the wallet only accepts those names and enforces `max` and `stack`. Names and icons are free JSON for
your client; public keys reach it through `GET /v1/config`. Without the key nothing is enforced.

## Inventory

Inventory counts stackable items per id. Per-item data (level, upgrades) belongs in the module's storage for now
(`ctx.Storage.ReadAsync<T>` / `WriteAsync<T>`).

## Admin

User detail in the panel shows balances, an adjust form (a reason is required) and the history.
API: `GET/POST /admin/api/users/{id}/economy`, `GET /admin/api/users/{id}/ledger`. The privacy export includes balances.

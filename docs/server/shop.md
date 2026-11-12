# Shop

Players buy with currencies and items from their wallet. The catalog is the config key `shop`. **Prices are always read from
the server**, never from the client. A purchase is one atomic economy transaction (history reason `shop_purchase`).

```json
{ "products": { "gems_small": {
    "name": "Small gem pack", "category": "gems", "sort": 1,
    "cost":  { "wallet": { "coins": 500 }, "items": { "ticket": 1 } },
    "gives": { "wallet": { "gems": 50 },  "items": { "card_fire": 1 } },
    "limit": { "per": "day", "count": 3 },
    "startUtc": "2026-12-01T00:00:00Z", "endUtc": "2026-12-08T00:00:00Z", "enabled": true } } }
```

| Field | Notes |
|---|---|
| `gives` | The only required field |
| `cost` | Omit for a free product (a daily gift: `limit` `day` 1) |
| `limit.per` | `day`, `week`, `month` (UTC) or `lifetime` |
| `startUtc` / `endUtc` | Time-limited offer. Products that already ended are hidden |
| `category`, `sort` | For your UI |

## API

- `GET /v1/shop?category=` lists products with `remaining` (what this player can still buy) and `active`.
- `POST /v1/shop/{id}/buy` with `{ "quantity": 1, "requestId": "..." }`.

Send a **fresh `requestId` per tap** and reuse it when retrying after a lost response: the purchase is applied once
(`alreadyApplied: true` on the retry).

Errors: `404 product_not_found`, `409 product_unavailable`, `purchase_limit_reached`, `insufficient_funds`, `insufficient_items`.

## Hooks and admin

Hook name `shop.buy` (before: refuse or change; after: e.g. unlock an achievement). Admin tab **Shop**,
`GET /admin/api/shop`, `GET /admin/api/users/{id}/shop`.

::: info Real money
Buying with real money (Bazaar, Myket, Google Play) is not built yet. It will credit the same wallet.
:::

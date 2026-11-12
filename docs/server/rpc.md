# RPC functions

An RPC is a server function the client can call: anything game-specific that is not a match, such as "open a loot box" or
"submit a score".

```csharp
public delegate Task<string> RpcHandler(RpcContext ctx, string payload);
```

You get the player in `ctx.UserId` and the request body as a string, and you return a string (usually JSON).
Clients call it as `POST /v1/rpc/{id}` with a Bearer token.

## Example: an atomic purchase

```csharp
public sealed class BuyCardRequest { public string Card { get; set; } = ""; }

private static async Task<string> BuyCard(RpcContext ctx, string payload)
{
    var req = JsonSerializer.Deserialize<BuyCardRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (string.IsNullOrWhiteSpace(req?.Card))
        throw new GameException("invalid_request", "card is required.");

    // 100 coins out, one card in, together or not at all
    var tx = new EconomyTransaction("buy_card").Currency("coins", -100).Item(req.Card, 1);
    var result = await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);

    return JsonSerializer.Serialize(new { coins = result.State.Wallet.GetValueOrDefault("coins"), items = result.State.Items });
}

// in Initialize:
init.RegisterRpc("buy_card", BuyCard);
```

## Example: a once-per-day reward

```csharp
var reward = await ctx.Config.GetAsync<DailyReward>("daily_reward", ctx.CancellationToken);
var coins = reward?.Coins ?? 100;

// The idempotency key makes "once per day" safe even if the client sends the request twice at once.
var tx = new EconomyTransaction("daily_reward", $"daily:{DateTime.UtcNow:yyyyMMdd}").Currency("coins", coins);
var result = await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);
if (result.AlreadyApplied)
    throw new GameException("already_claimed", "Daily reward already claimed today.");
```

## Example: validate then record a score

Clients cannot write leaderboards. A module checks the score, then records it:

```csharp
if (req.Score is < 0 or > 1_000_000)
    throw new GameException("invalid_score", "Score must be between 0 and 1000000.");
var board = await ctx.Leaderboards.SubmitAsync("weekly_score", ctx.UserId, req.Score, ctx.CancellationToken);
```

## From Unity

```csharp
var json = await client.Rpc.CallAsync("buy_card", "{\"card\":\"card_fire\"}");
var result = await client.Rpc.CallAsync<BuyCardResult>("buy_card", new { card = "card_fire" });
```

## Rules of thumb

- Never trust the payload. Validate everything; the client may be modified.
- Use idempotency keys for anything that pays out.
- Use `requestId`-style keys when a retry can happen.
- Keep one RPC per game action, not one generic RPC with a "type" field.
- Hooks can wrap an RPC too, with the operation name `HookOps.Rpc("id")` (`rpc:<id>`).

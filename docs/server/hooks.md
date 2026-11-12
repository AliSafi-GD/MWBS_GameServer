# Hooks

A module can run code **before** or **after** any built-in action. Use a before-hook to refuse or change a request, and an
after-hook to react, for example to give a gift after login.

```csharp
init.RegisterBefore(HookOps.ChatSend, (ctx, requestJson) => Task.FromResult(requestJson)); // may change the request or throw GameException
init.RegisterAfter(HookOps.AuthLogin, async (ctx, request, response) => { /* ... */ });
```

| | Gets | Can |
|---|---|---|
| **Before** | `HookContext`, the request as camelCase JSON | Return it changed or not; throw `GameException` to reject |
| **After** | `HookContext`, the request actually used, the response | Do side effects. It cannot change the response. An error is logged and ignored |

Several hooks on one operation run in registration order.

## Operation names

In `HookOps`:

| Name | Notes |
|---|---|
| `auth.login` | **After only.** Request `{ "provider": "device|googleplay|phone", "isNew": bool }`. `isNew` is a best guess, so use an idempotency key for gifts |
| `account.update`, `account.delete` | |
| `friends.add`, `friends.remove`, `friends.block`, `friends.unblock` | |
| `groups.create`, `update`, `delete`, `join`, `leave`, `invite`, `accept`, `kick`, `ban`, `unban`, `promote`, `demote`, `transfer` | prefix `groups.` |
| `parties.*` | create, join, ... |
| `chat.join`, `chat.send` | |
| `shop.buy`, `daily.claim`, `achievements.claim`, `quests.claim` | |
| `matchmaker.add`, `matches.create`, `matches.join` | |
| `rpc:<id>` | Wraps an RPC. Use `HookOps.Rpc("id")` |

## Examples

Refuse links in chat:

```csharp
init.RegisterBefore(HookOps.ChatSend, (ctx, requestJson) =>
{
    var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestJson) ?? new();
    if (doc.TryGetValue("text", out var text) && text.ValueKind == JsonValueKind.String
        && text.GetString()!.Contains("http://", StringComparison.OrdinalIgnoreCase))
        throw new GameException("invalid_message", "Links are not allowed in chat.");
    return Task.FromResult(requestJson);
});
```

One-time welcome gift (safe to run on every login thanks to the idempotency key):

```csharp
init.RegisterAfter(HookOps.AuthLogin, async (ctx, request, response) =>
{
    var tx = new EconomyTransaction("welcome_gift", "welcome").Currency("coins", 50);
    await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);
});
```

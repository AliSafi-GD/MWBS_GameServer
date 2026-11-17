using System.Text.Json;
using GameServer.Abstractions;

namespace MyGame.Modules;

/// <summary>The server creates this once at start and calls Initialize. Register everything the game needs here.</summary>
public sealed class MyGameModule : IServerModule
{
    public void Initialize(IModuleInitializer init)
    {
        // Clients call this with POST /v1/rpc/hello (SDK: client.Rpc.CallAsync("hello")).
        init.RegisterRpc("hello", (ctx, payload) =>
            Task.FromResult(JsonSerializer.Serialize(new { message = "hello " + ctx.UserId })));

        // A reward that can only be taken once: the idempotency key makes a second call do nothing.
        init.RegisterRpc("starter_pack", async (ctx, payload) =>
        {
            var tx = new EconomyTransaction("starter_pack", "starter_pack").Currency("coins", 100);
            var result = await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);
            return JsonSerializer.Serialize(new { alreadyTaken = result.AlreadyApplied, wallet = result.State.Wallet });
        });

        // Hooks run around built-in operations (names in HookOps), e.g. a gift after every login:
        //   init.RegisterAfter(HookOps.AuthLogin, async (ctx, request, response) => { ... });
        // Match logic: init.RegisterMatchHandler("mymode", () => new MyMatch()); and a "mymode" entry in config/matches.json.
    }
}

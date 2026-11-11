using System.Text.Json;
using GameServer.Abstractions;

namespace HelloGame.Modules;

public sealed class HelloModule : IServerModule
{
    public void Initialize(IModuleInitializer init)
    {
        init.RegisterRpc("hello", (ctx, payload) =>
            Task.FromResult(JsonSerializer.Serialize(new { message = $"hello {ctx.UserId}" })));

        init.RegisterRpc("claim_daily_reward", ClaimDailyReward);
        init.RegisterRpc("buy_card", BuyCard);
        init.RegisterRpc("submit_score", SubmitScore);

        // ---- match example ----
        // Mode "echo": define it in the "matches" config (see README) with "handler": "echo".
        init.RegisterMatchHandler("echo", () => new EchoMatch());

        // ---- hook examples ----
        // After a login: a one-time welcome gift. The idempotency key makes it safe to run on every login.
        init.RegisterAfter(HookOps.AuthLogin, async (ctx, request, response) =>
        {
            var tx = new EconomyTransaction("welcome_gift", "welcome").Currency("coins", 50);
            await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);
        });

        // Before a chat message is sent: a hook may change the request (here: trim long text) or throw GameException to refuse.
        init.RegisterBefore(HookOps.ChatSend, (ctx, requestJson) =>
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestJson) ?? new();
            if (doc.TryGetValue("text", out var text) && text.ValueKind == JsonValueKind.String
                && text.GetString()!.Contains("http://", StringComparison.OrdinalIgnoreCase))
                throw new GameException("invalid_message", "Links are not allowed in chat.");
            return Task.FromResult(requestJson);
        });
    }

    public sealed class DailyReward
    {
        public int Coins { get; set; } = 100;
    }

    private static async Task<string> ClaimDailyReward(RpcContext ctx, string payload)
    {
        // Reward amount comes from remote config key "daily_reward" ({ "coins": 50 }); 100 if the key is not set.
        var reward = await ctx.Config.GetAsync<DailyReward>("daily_reward", ctx.CancellationToken);
        var coins = reward?.Coins ?? 100;
        if (coins <= 0)
            throw new GameException("reward_disabled", "The daily reward is turned off.");

        // The idempotency key makes "once per day" safe even if the client sends the request twice at once.
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var tx = new EconomyTransaction("daily_reward", $"daily:{today}").Currency("coins", coins);

        var result = await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);
        if (result.AlreadyApplied)
            throw new GameException("already_claimed", "Daily reward already claimed today.");

        return JsonSerializer.Serialize(new { coins = result.State.Wallet["coins"] });
    }

    public sealed class ScoreRequest
    {
        public long Score { get; set; }
        public string? Tournament { get; set; }
    }

    // Clients cannot write leaderboards, so a module is where a score is checked (here: a simple sanity range)
    // and then recorded. Boards "weekly_score" and tournaments are defined in config (see the README).
    private static async Task<string> SubmitScore(RpcContext ctx, string payload)
    {
        var req = JsonSerializer.Deserialize<ScoreRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                  ?? new ScoreRequest();
        if (req.Score is < 0 or > 1_000_000)
            throw new GameException("invalid_score", "Score must be between 0 and 1000000.");

        var board = await ctx.Leaderboards.SubmitAsync("weekly_score", ctx.UserId, req.Score, ctx.CancellationToken);

        object? tournament = null;
        if (!string.IsNullOrEmpty(req.Tournament))
            tournament = await ctx.Leaderboards.SubmitTournamentAsync(req.Tournament, ctx.UserId, req.Score, ctx.CancellationToken);

        return JsonSerializer.Serialize(new { weekly = board, tournament });
    }

    public sealed class BuyCardRequest
    {
        public string Card { get; set; } = "";
    }

    // Shows an atomic purchase: 100 coins out and one card in, together or not at all.
    private static async Task<string> BuyCard(RpcContext ctx, string payload)
    {
        var req = JsonSerializer.Deserialize<BuyCardRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (string.IsNullOrWhiteSpace(req?.Card))
            throw new GameException("invalid_request", "card is required.");

        var tx = new EconomyTransaction("buy_card").Currency("coins", -100).Item(req.Card, 1);
        var result = await ctx.Economy.ApplyAsync(ctx.UserId, tx, ctx.CancellationToken);

        return JsonSerializer.Serialize(new { coins = result.State.Wallet.GetValueOrDefault("coins"), items = result.State.Items });
    }
}

/// <summary>
/// A minimal match handler. A real game keeps its rules here: the server calls these methods one at a time, so there
/// is no locking to do, whatever protocol (WebSocket, UDP...) the players use.
/// </summary>
public sealed class EchoMatch : MatchHandler
{
    public override async Task OnJoinAsync(IMatch match, MatchPlayer player) =>
        await match.BroadcastAsync(System.Text.Encoding.UTF8.GetBytes($"{player.Username} joined"));

    public override Task OnMessageAsync(IMatch match, MatchPlayer player, ReadOnlyMemory<byte> data) =>
        // Messages that may be lost (positions) can use MatchDelivery.Unreliable on a UDP transport.
        match.BroadcastAsync(data, MatchDelivery.Reliable);

    public override async Task OnTickAsync(IMatch match)
    {
        if (match.Tick % 100 == 0 && match.Connected.Count == 0)
            match.End("nobody_here");
        await Task.CompletedTask;
    }
}

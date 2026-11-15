using GameServer.Abstractions;

namespace GameServer.Host.Endpoints;

/// <summary>My wallet and inventory. Read-only: only modules change balances.</summary>
public static class EconomyEndpoints
{
    public static IEndpointRouteBuilder MapEconomy(this IEndpointRouteBuilder app)
    {
        // GET /v1/wallet : my currencies, e.g. { "wallet": { "coins": 500, "gems": 3 } } (read-only: only modules change balances)
        app.MapGet("/v1/wallet", async (HttpContext http, IEconomy economy, CancellationToken ct) =>
            Results.Ok(new { wallet = (await economy.GetAsync(http.UserId(), ct)).Wallet })).RequireAuthorization();

        // GET /v1/inventory : my items, e.g. { "items": { "card_fire": 2 } }
        app.MapGet("/v1/inventory", async (HttpContext http, IEconomy economy, CancellationToken ct) =>
            Results.Ok(new { items = (await economy.GetAsync(http.UserId(), ct)).Items })).RequireAuthorization();

        return app;
    }
}

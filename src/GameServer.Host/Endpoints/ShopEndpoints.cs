using GameServer.Core.Shop;

namespace GameServer.Host.Endpoints;

/// <summary>The shop (in-game currency purchases). The catalog is the config key "shop".</summary>
public static class ShopEndpoints
{
    private static string Me(HttpContext http) => http.User.FindFirst("sub")!.Value;

    public static IEndpointRouteBuilder MapShop(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1/shop").RequireAuthorization();

        // GET /v1/shop?category= : products on sale (and upcoming ones, with active=false) and how many I can still buy
        g.MapGet("", async (string? category, HttpContext http, ShopService shop, CancellationToken ct) =>
            Results.Ok(new { products = await shop.ListAsync(Me(http), category, ct) }));

        // POST /v1/shop/{id}/buy  { quantity, requestId }
        // requestId makes a retry safe: the same requestId is applied only once. 409 insufficient_funds / purchase_limit_reached.
        g.MapPost("/{id}/buy", async (string id, ShopBuyBody? body, HttpContext http, ShopService shop, CancellationToken ct) =>
            Results.Ok(new { purchase = await shop.BuyAsync(Me(http), new ShopBuyRequest(id, body?.Quantity, body?.RequestId), ct) }));

        return app;
    }
}

public sealed record ShopBuyBody(int? Quantity, string? RequestId);

public static class AdminShopEndpoints
{
    public static RouteGroupBuilder MapAdminShop(this RouteGroupBuilder admin)
    {
        // GET /admin/api/shop : the parsed catalog (edit it in the Config tab, key "shop")
        admin.MapGet("/shop", async (ShopService shop, CancellationToken ct) =>
            Results.Ok(new { products = (await shop.CatalogAsync(ct)).Products.Values.OrderBy(p => p.Sort).ThenBy(p => p.Id) }));

        // GET /admin/api/users/{id}/shop : what a player bought (current periods)
        admin.MapGet("/users/{id}/shop", async (string id, ShopService shop, CancellationToken ct) =>
            Results.Ok(new { purchases = await shop.PurchasesOfAsync(id, ct) }));

        return admin;
    }
}

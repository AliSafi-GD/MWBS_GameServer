using GameServer.Abstractions;
using GameServer.Core;

namespace GameServer.Host.Endpoints;

/// <summary>My notification inbox.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder app)
    {
        // GET /v1/notifications?limit=50&unread=false
        app.MapGet("/v1/notifications", async (int? limit, bool? unread, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
            Results.Ok(new { notifications = await notifications.ListAsync(http.UserId(), limit ?? 50, unread ?? false, false, ct) }))
            .RequireAuthorization();

        app.MapPost("/v1/notifications/{id:long}/read", async (long id, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
            await notifications.MarkReadAsync(http.UserId(), id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }))
            .RequireAuthorization();

        // POST /v1/notifications/{id}/claim : receive the attached reward (once). Returns the reward and my new balances.
        app.MapPost("/v1/notifications/{id:long}/claim", async (long id, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
        {
            var claim = await notifications.ClaimAsync(http.UserId(), id, ct);
            return Results.Ok(new { reward = claim.Reward, wallet = claim.State.Wallet, items = claim.State.Items });
        }).RequireAuthorization();

        app.MapDelete("/v1/notifications/{id:long}", async (long id, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
            await notifications.DeleteAsync(http.UserId(), id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }))
            .RequireAuthorization();

        return app;
    }
}

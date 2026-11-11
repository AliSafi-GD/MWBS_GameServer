using GameServer.Abstractions;
using GameServer.Core.Progression;

namespace GameServer.Host.Endpoints;

/// <summary>Stats, achievements, quests and the daily reward.</summary>
public static class ProgressEndpoints
{
    private static string Me(HttpContext http) => http.User.FindFirst("sub")!.Value;

    public static IEndpointRouteBuilder MapProgress(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1").RequireAuthorization();

        // GET /v1/stats : my lifetime stats
        g.MapGet("/stats", async (HttpContext http, IProgress progress, CancellationToken ct) =>
            Results.Ok(new { stats = await progress.GetStatsAsync(Me(http), ct) }));

        // GET /v1/achievements : progress of each; POST .../{id}/claim takes the reward once
        g.MapGet("/achievements", async (HttpContext http, ProgressService progress, CancellationToken ct) =>
            Results.Ok(new { achievements = await progress.ListAsync(GoalCatalog.Achievement, Me(http), ct) }));
        g.MapPost("/achievements/{id}/claim", async (string id, HttpContext http, ProgressService progress, CancellationToken ct) =>
            Results.Ok(new { claim = await progress.ClaimAsync(GoalCatalog.Achievement, Me(http), id, ct) }));

        // GET /v1/quests : day/week/month goals that reset; POST .../{id}/claim
        g.MapGet("/quests", async (HttpContext http, ProgressService progress, CancellationToken ct) =>
            Results.Ok(new { quests = await progress.ListAsync(GoalCatalog.Quest, Me(http), ct) }));
        g.MapPost("/quests/{id}/claim", async (string id, HttpContext http, ProgressService progress, CancellationToken ct) =>
            Results.Ok(new { claim = await progress.ClaimAsync(GoalCatalog.Quest, Me(http), id, ct) }));

        // GET /v1/daily : streak and what the next claim gives; POST /v1/daily/claim
        g.MapGet("/daily", async (HttpContext http, DailyRewardService daily, CancellationToken ct) =>
            Results.Ok(new { daily = await daily.GetAsync(Me(http), ct) }));
        g.MapPost("/daily/claim", async (HttpContext http, DailyRewardService daily, CancellationToken ct) =>
            Results.Ok(new { claim = await daily.ClaimAsync(Me(http), ct) }));

        return app;
    }
}

public sealed record AdminStatRequest(string Stat, long Value, string? Mode = null);

public static class AdminProgressEndpoints
{
    public static RouteGroupBuilder MapAdminProgress(this RouteGroupBuilder admin)
    {
        // GET /admin/api/progress : the parsed definitions (edit them in the Config tab)
        admin.MapGet("/progress", async (ProgressService progress, DailyRewardService daily, CancellationToken ct) =>
        {
            var (achievements, quests) = await progress.CatalogsAsync(ct, fresh: true);
            var dailyCatalog = await daily.CatalogAsync(ct);
            return Results.Ok(new
            {
                achievements = achievements.Goals.Values.OrderBy(g => g.Sort).ThenBy(g => g.Id),
                quests = quests.Goals.Values.OrderBy(g => g.Sort).ThenBy(g => g.Id),
                daily = new { dailyCatalog.Rewards, dailyCatalog.ResetOnMiss, dailyCatalog.UtcOffsetMinutes }
            });
        });

        // GET /admin/api/users/{id}/progress : lifetime stats and daily streak
        admin.MapGet("/users/{id}/progress", async (string id, IProgress progress, DailyRewardService daily, CancellationToken ct) =>
            Results.Ok(new { stats = await progress.GetStatsAsync(id, ct), daily = await daily.GetAsync(id, ct) }));

        // POST /admin/api/users/{id}/stats  { stat, value, mode: "add" | "max" } : support and testing
        admin.MapPost("/users/{id}/stats", async (string id, AdminStatRequest req, IProgress progress, CancellationToken ct) =>
        {
            var value = req.Mode == "max"
                ? await progress.SetStatMaxAsync(id, req.Stat, req.Value, ct)
                : await progress.AddStatAsync(id, req.Stat, req.Value, ct);
            return Results.Ok(new { stat = req.Stat, lifetime = value });
        });

        return admin;
    }
}

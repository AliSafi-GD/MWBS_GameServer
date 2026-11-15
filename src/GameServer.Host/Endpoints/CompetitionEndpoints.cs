using GameServer.Abstractions;
using GameServer.Core;

namespace GameServer.Host.Endpoints;

/// <summary>Leaderboards and tournaments. Read-only for clients (modules submit scores), except joining and claiming.</summary>
public static class CompetitionEndpoints
{
    public static IEndpointRouteBuilder MapCompetitions(this IEndpointRouteBuilder app)
    {
        // GET /v1/leaderboards/{id}?limit=50&around=0&period=current
        // Top entries plus "me" (my rank and score). around=N returns N entries above and below me instead of the top.
        // period: current (default), all, 2026-11-08, 2026-W45 or 2026-11 to read an earlier day/week/month.
        app.MapGet("/v1/leaderboards/{id}", async (string id, string? period, int? limit, int? around, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
            Results.Ok(await competitions.GetBoardAsync(id, period, limit ?? 50, around ?? 0, http.UserId(), ct))).RequireAuthorization();

        // GET /v1/tournaments : upcoming, active and recently ended tournaments, with whether I joined / claimed
        app.MapGet("/v1/tournaments", async (HttpContext http, ICompetitions competitions, CancellationToken ct) =>
            Results.Ok(new { tournaments = await competitions.ListTournamentsAsync(http.UserId(), ct) })).RequireAuthorization();

        // GET /v1/tournaments/{id}?limit=50&around=0 : the tournament and its standings
        app.MapGet("/v1/tournaments/{id}", async (string id, int? limit, int? around, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
        {
            var userId = http.UserId();
            var board = await competitions.GetBoardAsync("tournament:" + id, null, limit ?? 50, around ?? 0, userId, ct);
            var info = (await competitions.ListTournamentsAsync(userId, ct)).FirstOrDefault(t => t.Id == id);
            return Results.Ok(new { tournament = info, board });
        }).RequireAuthorization();

        // POST /v1/tournaments/{id}/join : pays the entry fee (once) and joins. 409 insufficient_funds / tournament_not_active
        app.MapPost("/v1/tournaments/{id}/join", async (string id, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
            Results.Ok(new { tournament = await competitions.JoinTournamentAsync(id, http.UserId(), ct) })).RequireAuthorization();

        // POST /v1/tournaments/{id}/claim : after the end, receive the reward for my final rank (once)
        app.MapPost("/v1/tournaments/{id}/claim", async (string id, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
            Results.Ok(await competitions.ClaimTournamentAsync(id, http.UserId(), ct))).RequireAuthorization();

        return app;
    }
}

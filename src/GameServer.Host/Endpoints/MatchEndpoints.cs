using GameServer.Core.Matches;

namespace GameServer.Host.Endpoints;

/// <summary>Matchmaker and matches over HTTP. The same actions exist on the realtime socket; both run the same hooks.</summary>
public static class MatchEndpoints
{
    private static string Me(HttpContext http) => http.User.FindFirst("sub")!.Value;

    public static IEndpointRouteBuilder MapMatches(this IEndpointRouteBuilder app)
    {
        // GET /v1/match/modes : modes and transports (so a client knows which protocol each mode uses)
        app.MapGet("/v1/match/modes", async (MatchActions actions, CancellationToken ct) => Results.Ok(await actions.ModesAsync(ct)));

        var g = app.MapGroup("/v1").RequireAuthorization();

        // POST /v1/matchmaker/tickets  { mode, properties, partyId }  (the player must also be connected to the realtime socket,
        // because the result arrives there as "matchmaker.matched")
        g.MapPost("/matchmaker/tickets", async (MatchmakerRequest req, HttpContext http, MatchActions actions, CancellationToken ct) =>
            Results.Ok(new { ticket = await actions.QueueAsync(Me(http), req, ct) }));

        g.MapGet("/matchmaker/tickets", (HttpContext http, MatchActions actions) =>
            Results.Ok(new { tickets = actions.Tickets(Me(http)) }));

        g.MapDelete("/matchmaker/tickets/{id}", async (string id, HttpContext http, MatchActions actions) =>
            await actions.UnqueueAsync(Me(http), id) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

        // POST /v1/matches  { mode, label, parameters }  : only for modes with allowClientCreate
        g.MapPost("/matches", async (CreateMatchRequest req, HttpContext http, MatchActions actions, CancellationToken ct) =>
            Results.Ok(await actions.CreateAsync(Me(http), req, ct)));

        // GET /v1/matches?mode=&limit=
        g.MapGet("/matches", (string? mode, int? limit, MatchActions actions) =>
            Results.Ok(new { matches = actions.List(mode, limit) }));

        g.MapGet("/matches/{id}", (string id, MatchActions actions) => Results.Ok(new { match = actions.Get(id) }));

        // POST /v1/matches/{id}/join : returns { transport, token, endpoint }; connect with that protocol within a few seconds
        g.MapPost("/matches/{id}/join", async (string id, HttpContext http, MatchActions actions, CancellationToken ct) =>
            Results.Ok(new { connect = await actions.JoinAsync(Me(http), new MatchRequest(id), ct) }));

        return app;
    }
}

/// <summary>Admin API for matches and the matchmaker. Mounted under /admin/api (X-Admin-Key).</summary>
public static class AdminMatchEndpoints
{
    public static RouteGroupBuilder MapAdminMatches(this RouteGroupBuilder admin)
    {
        // GET /admin/api/matches?mode=&limit=
        admin.MapGet("/matches", (string? mode, int? limit, MatchService matches, Matchmaker matchmaker) =>
            Results.Ok(new
            {
                running = matches.Count,
                queued = matchmaker.CountsByMode(),
                transports = matches.Transports.Select(t => t.Name),
                matches = matches.Snapshot(mode, limit ?? 100).Select(m => new
                {
                    m.Info.Id, m.Info.Mode, m.Info.Label, m.Info.Players, m.Info.Connected, m.Info.MaxPlayers, m.Info.Open, m.Info.CreatedUtc
                })
            }));

        admin.MapGet("/matches/{id}", (string id, MatchService matches) =>
        {
            var found = matches.Snapshot(null, 500).FirstOrDefault(m => m.Info.Id == id);
            return found.Info is null
                ? Results.NotFound(new { error = "match_not_found" })
                : Results.Ok(new { match = found.Info, players = found.Players });
        });

        admin.MapDelete("/matches/{id}", async (string id, MatchService matches, CancellationToken ct) =>
        {
            await matches.EndAsync(id, "ended_by_admin", ct);
            return Results.NoContent();
        });

        return admin;
    }
}

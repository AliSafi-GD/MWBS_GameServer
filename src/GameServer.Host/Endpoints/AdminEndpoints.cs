using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Admin;
using GameServer.Core.Identity;
using GameServer.Core.Realtime;
using GameServer.Core.Social;
using Microsoft.Extensions.Options;

namespace GameServer.Host.Endpoints;

/// <summary>
/// Admin API (used by the panel at /admin). Protected by the X-Admin-Key header. Every write is recorded in the audit
/// log by AdminAuditMiddleware. This file holds the group, its key/role check and the user, leaderboard, notification,
/// economy and config routes; the other admin areas are added by their own Map* methods (see MapAdmin).
/// </summary>
public static class AdminEndpoints
{
    public const string ApiPrefix = "/admin/api";

    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(ApiPrefix).RequireRateLimiting("admin").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var configured = http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.Key;
            if (string.IsNullOrEmpty(configured))
                return Results.Json(new { error = "admin_disabled", message = "Set GameServer__Admin__Key to enable the admin API." },
                    statusCode: StatusCodes.Status404NotFound);

            var given = http.Request.Headers["X-Admin-Key"].ToString();
            var actor = await http.RequestServices.GetRequiredService<AdminStore>().AuthenticateAsync(given, configured, http.RequestAborted);
            if (actor is null)
                return Results.Json(new { error = "forbidden", message = "Wrong admin key." }, statusCode: StatusCodes.Status403Forbidden);

            http.Items[AdminConsoleEndpoints.ActorItem] = actor; // also set when denied below, so the attempt is audited
            var relative = http.Request.Path.Value![ApiPrefix.Length..];
            if (!AdminPolicy.IsAllowed(actor.Role, http.Request.Method, relative))
                return Results.Json(new { error = "insufficient_role", message = $"Role {actor.Role} cannot do {http.Request.Method} {relative}." },
                    statusCode: StatusCodes.Status403Forbidden);

            return await next(context);
        });

        MapUsers(admin);
        MapLeaderboards(admin);
        MapNotifications(admin);
        MapEconomy(admin);
        MapConfig(admin);

        // groups/clans, friends, chat moderation, online players
        admin.MapAdminSocial();
        admin.MapAdminMatches();
        admin.MapAdminShop();
        admin.MapAdminProgress();
        admin.MapAdminConsole();

        admin.MapDelete("/users/{id}", async (string id, IUserStore users, AccountCleanup cleanup, CancellationToken ct) =>
        {
            await cleanup.BeforeDeleteAsync(id, ct);
            return await users.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
        });

        return app;
    }

    // ---- users ----------------------------------------------------------------------------------------------

    private static void MapUsers(RouteGroupBuilder admin)
    {
        // GET /admin/api/users?q=&limit=&offset=
        admin.MapGet("/users", async (string? q, int? limit, int? offset, IUserStore users, CancellationToken ct) =>
            Results.Ok(await users.SearchAsync(q, Math.Clamp(limit ?? 25, 1, 100), Math.Max(offset ?? 0, 0), ct)));

        // GET /admin/api/users/{id} : profile + logins (masked) + storage
        admin.MapGet("/users/{id}", async (string id, IUserStore users, CancellationToken ct) =>
        {
            var export = await users.ExportAsync(id, ct);
            return export is null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(export);
        });

        // GET /admin/api/users/{id}/export : same data as a downloadable file (for privacy requests)
        admin.MapGet("/users/{id}/export", async (string id, HttpContext http, IUserStore users, IEconomyStore economy, CancellationToken ct) =>
        {
            var export = await users.ExportAsync(id, ct);
            if (export is null)
                return Results.NotFound(new { error = "not_found" });

            http.Response.Headers.ContentDisposition = $"attachment; filename=\"user-{id}.json\"";
            return Results.Ok(new { export.User, export.Devices, export.Identities, export.Storage, economy = await economy.GetAsync(id, ct) });
        });

        // PUT /admin/api/users/{id} : edit profile fields
        admin.MapPut("/users/{id}", async (string id, ProfileUpdate update, IUserStore users, CancellationToken ct) =>
        {
            var user = await users.UpdateProfileAsync(id, ProfileValidator.Normalize(update), ct);
            return user is null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(user);
        });

        admin.MapPost("/users/{id}/ban", async (string id, IUserStore users, IRealtime realtime, CancellationToken ct) =>
        {
            if (!await users.SetDisabledAsync(id, true, ct))
                return Results.NotFound(new { error = "not_found" });
            await realtime.DisconnectUserAsync(id, "banned"); // close open chat connections right away
            return Results.NoContent();
        });

        admin.MapPost("/users/{id}/unban", async (string id, IUserStore users, CancellationToken ct) =>
            await users.SetDisabledAsync(id, false, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));
    }

    // ---- leaderboards ---------------------------------------------------------------------------------------

    private static void MapLeaderboards(RouteGroupBuilder admin)
    {
        // GET /admin/api/leaderboards/{id}?period=&limit=  (id "tournament:<id>" reads a tournament)
        admin.MapGet("/leaderboards/{id}", async (string id, string? period, int? limit, ICompetitions competitions, CancellationToken ct) =>
            Results.Ok(await competitions.GetBoardAsync(id, period, limit ?? 50, 0, null, ct)));

        // DELETE /admin/api/leaderboards/{id}/scores/{userId} : remove a player's scores (all periods), e.g. a cheater
        admin.MapDelete("/leaderboards/{id}/scores/{userId}", async (string id, string userId, ICompetitions competitions, CancellationToken ct) =>
            await competitions.DeleteScoreAsync(id, userId, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));
    }

    // ---- notifications --------------------------------------------------------------------------------------

    private static void MapNotifications(RouteGroupBuilder admin)
    {
        // GET /admin/api/users/{id}/notifications : a player's inbox, including expired ones (for support)
        admin.MapGet("/users/{id}/notifications", async (string id, int? limit, INotificationStore notifications, CancellationToken ct) =>
            Results.Ok(new { notifications = await notifications.ListAsync(id, limit ?? 50, false, true, ct) }));

        // POST /admin/api/users/{id}/notifications
        //   body: { "subject": "...", "body": "...", "reward": { "wallet": { "coins": 100 }, "items": {} }, "expiresInDays": 30 }
        admin.MapPost("/users/{id}/notifications", async (string id, AdminNotificationRequest req, INotificationStore notifications, CancellationToken ct) =>
            Results.Ok(new { id = await notifications.SendAsync(id, req.Subject ?? "", req.Body, req.Reward, ExpiryFrom(req.ExpiresInDays), ct) }));

        // POST /admin/api/notifications/broadcast : same body, sent to every active player
        admin.MapPost("/notifications/broadcast", async (AdminNotificationRequest req, INotificationStore notifications, CancellationToken ct) =>
            Results.Ok(new { sent = await notifications.BroadcastAsync(req.Subject ?? "", req.Body, req.Reward, ExpiryFrom(req.ExpiresInDays), ct) }));
    }

    private static DateTime? ExpiryFrom(int? days) => days is > 0 ? DateTime.UtcNow.AddDays(Math.Min(days.Value, 3650)) : null;

    // ---- wallet and inventory -------------------------------------------------------------------------------

    private static void MapEconomy(RouteGroupBuilder admin)
    {
        // GET /admin/api/users/{id}/economy : balances and items
        admin.MapGet("/users/{id}/economy", async (string id, IEconomyStore economy, CancellationToken ct) =>
            Results.Ok(await economy.GetAsync(id, ct)));

        // GET /admin/api/users/{id}/ledger?limit=50&before=<id> : history, newest first
        admin.MapGet("/users/{id}/ledger", async (string id, int? limit, long? before, IEconomyStore economy, CancellationToken ct) =>
            Results.Ok(new { items = await economy.LedgerAsync(id, Math.Clamp(limit ?? 50, 1, 200), before, ct) }));

        // POST /admin/api/users/{id}/economy   body: { "wallet": { "coins": 100 }, "items": { "card_fire": -1 }, "reason": "compensation" }
        // Negative numbers take away. Saved in the history as "admin: <reason>". Fails with 409 if a balance would go below 0.
        admin.MapPost("/users/{id}/economy", async (string id, EconomyAdjustRequest req, IEconomyStore economy, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason))
                throw new GameException("invalid_transaction", "A reason is required.");

            var tx = new EconomyTransaction("admin: " + req.Reason.Trim());
            foreach (var (name, delta) in req.Wallet ?? new()) tx.Currency(name, delta);
            foreach (var (name, delta) in req.Items ?? new()) tx.Item(name, delta);

            return Results.Ok((await economy.ApplyAsync(id, tx, ct)).State);
        });
    }

    // ---- remote config --------------------------------------------------------------------------------------

    private static void MapConfig(RouteGroupBuilder admin)
    {
        // GET /admin/api/config : every key (public and server-only)
        admin.MapGet("/config", async (IConfigStore config, CancellationToken ct) =>
            Results.Ok(new { items = await config.ListAsync(ct) }));

        // PUT /admin/api/config/{key}   body: { "value": <any JSON>, "public": true|false }
        admin.MapPut("/config/{key}", async (string key, ConfigWriteRequest req, HttpContext http, IConfigStore config, AdminStore adminStore, CancellationToken ct) =>
        {
            ConfigRules.ValidateKey(key);
            ConfigRules.ValidateValue(key, req.Value);
            var isPublic = req.Public || ConfigRules.IsAlwaysPublic(key);
            var previous = await config.GetAsync(key, ct);
            if (previous is not null && (previous.IsPublic != isPublic || previous.Value.GetRawText() != req.Value.GetRawText()))
                await adminStore.AddConfigHistoryAsync(previous, AdminConsoleEndpoints.Actor(http).Name, ct);
            return Results.Ok(await config.SetAsync(key, req.Value, isPublic, ct));
        });

        admin.MapDelete("/config/{key}", async (string key, HttpContext http, IConfigStore config, AdminStore adminStore, CancellationToken ct) =>
        {
            var previous = await config.GetAsync(key, ct);
            if (previous is not null)
                await adminStore.AddConfigHistoryAsync(previous, AdminConsoleEndpoints.Actor(http).Name + " (delete)", ct);
            return await config.DeleteAsync(key, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
        });
    }
}

public sealed record AdminNotificationRequest(string? Subject, string? Body, NotificationReward? Reward, int? ExpiresInDays);
public sealed record EconomyAdjustRequest(Dictionary<string, long>? Wallet, Dictionary<string, long>? Items, string? Reason);
public sealed record ConfigWriteRequest(JsonElement Value, bool Public = false);

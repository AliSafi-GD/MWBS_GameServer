using System.Diagnostics;
using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Admin;
using GameServer.Core.Matches;
using GameServer.Core.Realtime;

namespace GameServer.Host.Endpoints;

/// <summary>
/// Admin console extras: who am I, admin accounts, audit log, live dashboard, player storage and timeline,
/// scheduled broadcasts (live-ops), config history, modules and an RPC tester. Mounted under /admin/api.
/// Roles are checked by the filter in Program.cs (see <see cref="AdminPolicy"/>).
/// </summary>
public static class AdminConsoleEndpoints
{
    public const string ActorItem = "admin.actor";

    public static AdminActor Actor(HttpContext http) => (AdminActor)http.Items[ActorItem]!;

    public static RouteGroupBuilder MapAdminConsole(this RouteGroupBuilder admin)
    {
        // ---- who am I ----

        admin.MapGet("/me", (HttpContext http) =>
        {
            var actor = Actor(http);
            return Results.Ok(new { name = actor.Name, role = actor.Role.ToString(), root = actor.Id == "root" });
        });

        // ---- admin accounts (Admin role only) ----

        admin.MapGet("/admins", async (AdminStore store, CancellationToken ct) =>
            Results.Ok(new { items = await store.ListAdminsAsync(ct) }));

        // POST /admin/api/admins  { name, role: "Admin"|"Support"|"ReadOnly" } -> { account, key }  (the key is shown only once)
        admin.MapPost("/admins", async (AdminCreateRequest req, HttpContext http, AdminStore store, CancellationToken ct) =>
        {
            if (!AdminPolicy.TryParseRole(req.Role, out var role))
                throw new GameException("invalid_role", "Role must be Admin, Support or ReadOnly.");
            var (account, key) = await store.CreateAdminAsync((req.Name ?? "").Trim(), role, Actor(http).Name, ct);
            return Results.Ok(new { account, key });
        });

        // PUT /admin/api/admins/{id}  { role?, disabled? }
        admin.MapPut("/admins/{id}", async (string id, AdminUpdateRequest req, AdminStore store, CancellationToken ct) =>
        {
            AdminRole? role = null;
            if (req.Role is not null)
            {
                if (!AdminPolicy.TryParseRole(req.Role, out var parsed))
                    throw new GameException("invalid_role", "Role must be Admin, Support or ReadOnly.");
                role = parsed;
            }
            return await store.UpdateAdminAsync(id, role, req.Disabled, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
        });

        // POST /admin/api/admins/{id}/reset-key -> { key }
        admin.MapPost("/admins/{id}/reset-key", async (string id, AdminStore store, CancellationToken ct) =>
        {
            var key = await store.ResetKeyAsync(id, ct);
            return key is null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(new { key });
        });

        admin.MapDelete("/admins/{id}", async (string id, AdminStore store, CancellationToken ct) =>
            await store.DeleteAdminAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

        // ---- audit log ----

        // GET /admin/api/audit?actor=&q=&before=<id>&limit=100
        admin.MapGet("/audit", async (string? actor, string? q, long? before, int? limit, AdminStore store, CancellationToken ct) =>
            Results.Ok(new { items = await store.ListAuditAsync(actor, q, before, limit ?? 100, ct) }));

        // ---- dashboard ----

        // GET /admin/api/dashboard : live numbers + 24h totals
        admin.MapGet("/dashboard", async (ConnectionManager connections, MatchService matches, Matchmaker matchmaker, AdminStore store, CancellationToken ct) =>
        {
            var (conns, users) = connections.Counts();
            var queue = matchmaker.CountsByMode();
            return Results.Ok(new
            {
                online = new { connections = conns, users },
                matches = matches.Count,
                queue = new { total = queue.Values.Sum(), byMode = queue },
                totals = await store.TotalsAsync(ct),
                serverTimeUtc = DateTime.UtcNow
            });
        });

        // GET /admin/api/dashboard/series?minutes=180 : online / matches / queue over time
        admin.MapGet("/dashboard/series", async (int? minutes, AdminStore store, CancellationToken ct) =>
            Results.Ok(new { items = await store.SeriesAsync(minutes ?? 180, ct) }));

        // ---- player storage ----

        admin.MapGet("/users/{id}/storage", async (string id, AdminStore store, CancellationToken ct) =>
            Results.Ok(new { items = await store.ListStorageAsync(id, ct) }));

        // PUT /admin/api/users/{id}/storage/{key}   body: { "value": <any JSON> }
        admin.MapPut("/users/{id}/storage/{key}", async (string id, string key, StorageWriteRequest req, AdminStore store, CancellationToken ct) =>
        {
            await store.WriteStorageAsync(id, key, req.Value, ct);
            return Results.NoContent();
        });

        admin.MapDelete("/users/{id}/storage/{key}", async (string id, string key, AdminStore store, CancellationToken ct) =>
            await store.DeleteStorageAsync(id, key, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

        // ---- player 360: one merged timeline (wallet changes, inbox, chat, admin actions) ----

        admin.MapGet("/users/{id}/timeline", async (string id, int? limit, IUserStore users, AdminStore store, CancellationToken ct) =>
        {
            if (await users.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "not_found" });
            return Results.Ok(new { items = await store.TimelineAsync(id, limit ?? 100, ct) });
        });

        // ---- live-ops: scheduled broadcasts ----

        admin.MapGet("/liveops/broadcasts", async (int? limit, AdminStore store, CancellationToken ct) =>
            Results.Ok(new { items = await store.ListBroadcastsAsync(limit ?? 50, ct) }));

        // POST /admin/api/liveops/broadcasts
        //   { runAtUtc?: "2026-12-01T10:00:00Z" (default now), subject, body, reward, audience: "all"|"online", expiresInDays }
        admin.MapPost("/liveops/broadcasts", async (ScheduleBroadcastRequest req, HttpContext http, AdminStore store, CancellationToken ct) =>
        {
            ValidateReward(req.Reward);
            var runAt = req.RunAtUtc ?? DateTime.UtcNow;
            if (runAt.Kind == DateTimeKind.Local)
                runAt = runAt.ToUniversalTime();
            var item = await store.ScheduleAsync(runAt, req.Subject ?? "", req.Body, req.Reward,
                string.IsNullOrWhiteSpace(req.Audience) ? "all" : req.Audience.Trim().ToLowerInvariant(), req.ExpiresInDays,
                Actor(http).Name, ct);
            return Results.Ok(item);
        });

        admin.MapDelete("/liveops/broadcasts/{id:long}", async (long id, AdminStore store, CancellationToken ct) =>
            await store.DeleteBroadcastAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

        // ---- config history ----

        admin.MapGet("/config/{key}/history", async (string key, AdminStore store, CancellationToken ct) =>
            Results.Ok(new { items = await store.ConfigHistoryAsync(key, 50, ct) }));

        // POST /admin/api/config/{key}/rollback/{historyId} : put an older value back (the current one is kept in history)
        admin.MapPost("/config/{key}/rollback/{historyId:long}", async (string key, long historyId, HttpContext http, IConfigStore config, AdminStore store, CancellationToken ct) =>
        {
            var old = await store.ConfigHistoryItemAsync(key, historyId, ct);
            if (old?.Value is not { } value)
                return Results.NotFound(new { error = "not_found" });

            ConfigRules.ValidateKey(key);
            ConfigRules.ValidateValue(key, value);
            var current = await config.GetAsync(key, ct);
            if (current is not null)
                await store.AddConfigHistoryAsync(current, Actor(http).Name + " (rollback)", ct);
            return Results.Ok(await config.SetAsync(key, value, old.IsPublic || ConfigRules.IsAlwaysPublic(key), ct));
        });

        // ---- modules and RPC tester ----

        // GET /admin/api/modules : what the loaded game modules registered
        admin.MapGet("/modules", (ModuleRegistry registry, MatchService matches) =>
        {
            return Results.Ok(new
            {
                rpcs = registry.RpcIds.OrderBy(x => x, StringComparer.Ordinal),
                matchHandlers = registry.MatchHandlerNames.OrderBy(x => x, StringComparer.Ordinal),
                rpcHooks = registry.RpcIds.Where(id => registry.HasHooks(HookOps.Rpc(id))).OrderBy(x => x, StringComparer.Ordinal),
                transports = matches.Transports.Select(t => t.Name)
            });
        });

        // POST /admin/api/rpc-test   body: { userId, id, payload }  runs a module RPC as that player (hooks included)
        admin.MapPost("/rpc-test", async (
            RpcTestRequest req, HttpContext http, IUserStore users, ModuleRegistry registry, HookRunner hooks, IStorage storage,
            IConfig config, IEconomy economy, ILeaderboards leaderboards, INotifications notifications, ISocial social,
            IChat chat, IMatches matches, IProgress progress, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Id) || !registry.TryGetRpc(req.Id, out var handler))
                return Results.NotFound(new { error = "rpc_not_found" });
            if (string.IsNullOrWhiteSpace(req.UserId) || await users.GetAsync(req.UserId, ct) is null)
                return Results.NotFound(new { error = "user_not_found" });

            var watch = Stopwatch.StartNew();
            try
            {
                var result = await hooks.RunRawAsync(HookOps.Rpc(req.Id), req.UserId, req.Payload ?? "",
                    p => handler(new RpcContext(req.UserId, storage, config, economy, leaderboards, notifications, social, chat, matches, progress, ct), p),
                    ct);
                return Results.Ok(new { ok = true, ms = watch.ElapsedMilliseconds, result });
            }
            catch (GameException ex)
            {
                return Results.Ok(new { ok = false, ms = watch.ElapsedMilliseconds, error = ex.Code, message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new { ok = false, ms = watch.ElapsedMilliseconds, error = "exception", message = ex.Message });
            }
        });

        return admin;
    }

    private static void ValidateReward(NotificationReward? reward)
    {
        if (reward is null)
            return;
        foreach (var (name, amount) in (reward.Wallet ?? new()).Concat(reward.Items ?? new()))
            if (string.IsNullOrWhiteSpace(name) || amount <= 0)
                throw new GameException("invalid_reward", "Reward amounts must be positive and names must not be empty.");
    }
}

public sealed record AdminCreateRequest(string? Name, string? Role);
public sealed record AdminUpdateRequest(string? Role, bool? Disabled);
public sealed record StorageWriteRequest(JsonElement Value);
public sealed record ScheduleBroadcastRequest(
    DateTime? RunAtUtc, string? Subject, string? Body, NotificationReward? Reward, string? Audience, int? ExpiresInDays);
public sealed record RpcTestRequest(string? UserId, string? Id, string? Payload);

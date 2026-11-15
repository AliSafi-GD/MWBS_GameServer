using GameServer.Abstractions;
using GameServer.Core;

namespace GameServer.Host.Endpoints;

/// <summary>Calls into game modules.</summary>
public static class RpcEndpoints
{
    public static IEndpointRouteBuilder MapRpc(this IEndpointRouteBuilder app)
    {
        // POST /v1/rpc/{id}   header: Authorization: Bearer <token>   body: any string (usually JSON)
        app.MapPost("/v1/rpc/{id}", async (string id, HttpContext http, ModuleRegistry registry, HookRunner hooks, IStorage storage, IConfig config, IEconomy economy, ILeaderboards leaderboards, INotifications notifications, ISocial social, IChat chat, IMatches matches, IProgress progress) =>
        {
            if (!registry.TryGetRpc(id, out var handler))
                return Results.NotFound(new { error = "rpc_not_found" });

            string payload;
            using (var reader = new StreamReader(http.Request.Body))
                payload = await reader.ReadToEndAsync();

            var userId = http.UserId();
            var result = await hooks.RunRawAsync(HookOps.Rpc(id), userId, payload,
                p => handler(new RpcContext(userId, storage, config, economy, leaderboards, notifications, social, chat, matches, progress, http.RequestAborted), p),
                http.RequestAborted);
            return Results.Text(result, "application/json");
        }).RequireAuthorization();

        return app;
    }
}

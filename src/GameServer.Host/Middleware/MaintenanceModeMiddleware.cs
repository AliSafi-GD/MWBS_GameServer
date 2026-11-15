using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core;

namespace GameServer.Host.Middleware;

public static class MaintenanceModeMiddleware
{
    /// <summary>
    /// Maintenance mode: while config key "maintenance" has enabled=true, the game API answers 503.
    /// Config, version check, health and the admin API stay reachable so clients can show the message.
    /// </summary>
    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/v1")
                && !path.StartsWithSegments("/v1/config")
                && !path.StartsWithSegments("/v1/version-check"))
            {
                var entry = await context.RequestServices.GetRequiredService<IConfigStore>()
                    .GetAsync(ConfigRules.Maintenance, context.RequestAborted);

                if (entry?.Value is { ValueKind: JsonValueKind.Object } value
                    && value.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                {
                    JsonElement? message = value.TryGetProperty("message", out var m) ? m.Clone() : null;
                    JsonElement? until = value.TryGetProperty("until", out var u) ? u.Clone() : null;
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "maintenance", message, until });
                    return;
                }
            }

            await next();
        });
}

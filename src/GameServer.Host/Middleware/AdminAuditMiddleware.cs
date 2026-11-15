using System.Text;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Admin;
using GameServer.Host.Endpoints;

namespace GameServer.Host.Middleware;

public static class AdminAuditMiddleware
{
    /// <summary>
    /// Audit log: remember the start of the body of every admin write, run the request, then store who did what and
    /// the result. The actor is put into HttpContext.Items by the admin API group (see AdminEndpoints).
    /// </summary>
    public static IApplicationBuilder UseAdminAudit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            if (!context.Request.Path.StartsWithSegments(AdminEndpoints.ApiPrefix) || method is "GET" or "HEAD" or "OPTIONS")
            {
                await next();
                return;
            }

            string? body = null;
            context.Request.EnableBuffering();
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 1024, leaveOpen: true))
            {
                var buffer = new char[4096];
                var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                body = new string(buffer, 0, read);
            }
            context.Request.Body.Position = 0;

            var status = 500;
            try
            {
                await next();
                status = context.Response.StatusCode;
            }
            catch (GameException ex)
            {
                status = GameExceptionMiddleware.StatusFor(ex.Code);
                throw;
            }
            finally
            {
                if (context.Items[AdminConsoleEndpoints.ActorItem] is AdminActor actor)
                {
                    try
                    {
                        await context.RequestServices.GetRequiredService<AdminStore>().AuditAsync(
                            actor, method, context.Request.Path.Value![AdminEndpoints.ApiPrefix.Length..], body, status,
                            context.Connection.RemoteIpAddress?.ToString());
                    }
                    catch (Exception ex)
                    {
                        context.RequestServices.GetRequiredService<ILogger<AdminStore>>().LogWarning(ex, "Could not write the admin audit log.");
                    }
                }
            }
        });
}

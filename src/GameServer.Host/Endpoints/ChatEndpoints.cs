using GameServer.Core.Realtime;

namespace GameServer.Host.Endpoints;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        // GET /v1/realtime : which transports this server was built with and how to reach them (no login needed).
        app.MapGet("/v1/realtime", (IServiceProvider services, Microsoft.Extensions.Options.IOptions<RealtimeOptions> options) =>
            Results.Ok(new
            {
                transports = services.GetServices<ITransport>().Select(t => t.Describe()).ToArray(),
                maxMessageBytes = options.Value.MaxMessageBytes,
                protocol = "After connecting send {\"type\":\"auth\",\"data\":{\"token\":\"<jwt>\"}} within a few seconds."
            }));

        // GET /v1/chat/history?channel=dm:<userId>&limit=50&before=<messageId>
        app.MapGet("/v1/chat/history", async (string channel, int? limit, long? before, HttpContext http, ChatService chat, CancellationToken ct) =>
            Results.Ok(new { items = await chat.HistoryAsync(http.User.FindFirst("sub")!.Value, channel, Math.Clamp(limit ?? 50, 1, 100), before, ct) }))
            .RequireAuthorization();

        // GET /v1/online?ids=a,b,c : which of these players are connected right now (max 200 ids)
        app.MapGet("/v1/online", (string ids, IRealtime realtime) =>
        {
            var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(200).ToArray();
            return Results.Ok(new { online = realtime.OnlineAmong(list) });
        }).RequireAuthorization();

        return app;
    }
}

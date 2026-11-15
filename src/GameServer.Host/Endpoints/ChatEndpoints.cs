using GameServer.Core.Realtime;

namespace GameServer.Host.Endpoints;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        // GET /v1/realtime : which transports this server was built with and how to reach them (no login needed).
        app.MapGet("/v1/realtime", ([Microsoft.AspNetCore.Mvc.FromServices] IEnumerable<ITransport> transports, Microsoft.Extensions.Options.IOptions<RealtimeOptions> options) =>
            Results.Ok(new
            {
                transports = transports.Select(t => t.Describe()).ToArray(),
                maxMessageBytes = options.Value.MaxMessageBytes,
                protocol = "After connecting send {\"type\":\"auth\",\"data\":{\"token\":\"<jwt>\"}} within a few seconds."
            }));

        // GET /v1/chat/history?channel=dm:<userId>&limit=50&before=<messageId>
        app.MapGet("/v1/chat/history", async (string channel, int? limit, long? before, HttpContext http, ChatService chat, CancellationToken ct) =>
            Results.Ok(new { items = await chat.HistoryAsync(http.UserId(), channel, Math.Clamp(limit ?? 50, 1, 100), before, ct) }))
            .RequireAuthorization();

        // GET /v1/chat/conversations?limit=50 : my direct chats and clans, newest first, with the last message and unread counts
        app.MapGet("/v1/chat/conversations", async (int? limit, HttpContext http, ChatService chat, CancellationToken ct) =>
            Results.Ok(new { items = await chat.ConversationsAsync(http.UserId(), Math.Clamp(limit ?? 50, 1, 200), ct) }))
            .RequireAuthorization();

        // POST /v1/chat/read   body: { "channel": "dm:<userId>" | "group:<id>", "messageId": 123 } : read up to that message
        app.MapPost("/v1/chat/read", async (ChatReadRequest req, HttpContext http, ChatService chat, CancellationToken ct) =>
        {
            await chat.MarkReadAsync(http.UserId(), req.Channel, req.MessageId, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // GET /v1/online?ids=a,b,c : which of these players are connected right now (max 200 ids)
        app.MapGet("/v1/online", (string ids, IRealtime realtime) =>
        {
            var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(200).ToArray();
            return Results.Ok(new { online = realtime.OnlineAmong(list) });
        }).RequireAuthorization();

        return app;
    }
}

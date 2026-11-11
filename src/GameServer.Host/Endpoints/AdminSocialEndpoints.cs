using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Realtime;
using GameServer.Core.Social;

namespace GameServer.Host.Endpoints;

/// <summary>Admin API for groups/clans, friends, chat and online players. Mounted under /admin/api (X-Admin-Key).</summary>
public static class AdminSocialEndpoints
{
    public static RouteGroupBuilder MapAdminSocial(this RouteGroupBuilder admin)
    {
        // ---- groups / clans ----

        // GET /admin/api/groups?q=&type=&limit=
        admin.MapGet("/groups", async (string? q, string? type, int? limit, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { groups = await groups.SearchAsync(q, type, Math.Clamp(limit ?? 50, 1, 200), ct) }));

        // GET /admin/api/groups/{id} : the group and all its members (also pending and banned ones)
        admin.MapGet("/groups/{id}", async (string id, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { group = await groups.GetAsync(id, ct), members = await groups.AdminMembersAsync(id, 500, ct) }));

        // PUT /admin/api/groups/{id}   body: { name, description, avatar, open, maxMembers }
        admin.MapPut("/groups/{id}", async (string id, GroupUpdateBody body, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { group = await groups.AdminUpdateAsync(
                id, new GroupUpdateRequest(id, body.Name, body.Description, body.Avatar, body.Open, body.MaxMembers), ct) }));

        admin.MapDelete("/groups/{id}", async (string id, GroupService groups, CancellationToken ct) =>
        {
            await groups.AdminDeleteAsync(id, ct);
            return Results.NoContent();
        });

        admin.MapPost("/groups/{id}/members/{userId}/kick", async (string id, string userId, GroupService groups, CancellationToken ct) =>
        {
            await groups.AdminRemoveMemberAsync(id, userId, ban: false, ct);
            return Results.NoContent();
        });

        admin.MapPost("/groups/{id}/members/{userId}/ban", async (string id, string userId, GroupService groups, CancellationToken ct) =>
        {
            await groups.AdminRemoveMemberAsync(id, userId, ban: true, ct);
            return Results.NoContent();
        });

        admin.MapPost("/groups/{id}/transfer/{userId}", async (string id, string userId, GroupService groups, CancellationToken ct) =>
        {
            await groups.AdminTransferAsync(id, userId, ct);
            return Results.NoContent();
        });

        // GET /admin/api/users/{id}/groups : groups a player belongs to
        admin.MapGet("/users/{id}/groups", async (string id, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { groups = await groups.MineAsync(id, ct) }));

        // ---- friends ----

        // GET /admin/api/users/{id}/friends?state=friend|invite_sent|invite_received|blocked
        admin.MapGet("/users/{id}/friends", async (string id, string? state, int? limit, FriendsService friends, CancellationToken ct) =>
        {
            FriendState? parsed = state?.ToLowerInvariant() switch
            {
                null or "" => null,
                "friend" => FriendState.Friend,
                "invite_sent" => FriendState.InviteSent,
                "invite_received" => FriendState.InviteReceived,
                "blocked" => FriendState.Blocked,
                _ => throw new GameException("invalid_request", "state must be friend, invite_sent, invite_received or blocked.")
            };
            return Results.Ok(new { friends = await friends.ListAsync(id, parsed, Math.Clamp(limit ?? 200, 1, 200), ct) });
        });

        // DELETE /admin/api/users/{id}/friends/{otherId} : remove a friendship or invite
        admin.MapDelete("/users/{id}/friends/{otherId}", async (string id, string otherId, FriendsService friends, CancellationToken ct) =>
            await friends.RemoveAsync(id, otherId, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

        // ---- chat ----

        // GET /admin/api/chat/channels : channels with stored messages
        admin.MapGet("/chat/channels", async (int? limit, ChatService chat, CancellationToken ct) =>
            Results.Ok(new { channels = await chat.AdminChannelsAsync(limit ?? 50, ct) }));

        // GET /admin/api/chat/messages?channel=group:<id>&limit=50&before=<id>   (direct messages: dm:<idA>:<idB>)
        admin.MapGet("/chat/messages", async (string channel, int? limit, long? before, ChatService chat, CancellationToken ct) =>
            Results.Ok(new { items = await chat.AdminHistoryAsync(channel, Math.Clamp(limit ?? 50, 1, 100), before, ct) }));

        admin.MapDelete("/chat/messages/{id:long}", async (long id, ChatService chat, CancellationToken ct) =>
            await chat.AdminDeleteMessageAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

        // POST /admin/api/chat/send   body: { "channel": "room:global", "text": "Server restarts in 5 minutes" }
        admin.MapPost("/chat/send", async (AdminChatSend req, ChatService chat, CancellationToken ct) =>
        {
            await chat.SendSystemAsync(req.Channel ?? "", req.Text ?? "", ct);
            return Results.NoContent();
        });

        // GET /admin/api/chat/mutes
        admin.MapGet("/chat/mutes", async (int? limit, ChatService chat, IUserStore users, CancellationToken ct) =>
        {
            var mutes = await chat.MutesAsync(limit ?? 100, ct);
            var names = mutes.Count == 0
                ? new Dictionary<string, string>()
                : (await users.GetPublicAsync(mutes.Select(m => m.UserId).ToArray(), ct)).ToDictionary(p => p.Id, p => p.Username);
            return Results.Ok(new
            {
                mutes = mutes.Select(m => new
                {
                    m.UserId, username = names.GetValueOrDefault(m.UserId), m.UntilUtc, m.Reason, m.CreatedUtc
                })
            });
        });

        // POST /admin/api/users/{id}/mute   body: { "minutes": 60, "reason": "spam" }  (no minutes = until lifted)
        admin.MapPost("/users/{id}/mute", async (string id, AdminMuteRequest req, ChatService chat, CancellationToken ct) =>
        {
            await chat.MuteAsync(id, req.Minutes, req.Reason, ct);
            return Results.NoContent();
        });

        admin.MapDelete("/users/{id}/mute", async (string id, ChatService chat, CancellationToken ct) =>
            await chat.UnmuteAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

        // ---- online players ----

        // GET /admin/api/online?limit=100 : open connections and who is connected
        admin.MapGet("/online", async (int? limit, ConnectionManager manager, IUserStore users, CancellationToken ct) =>
        {
            var (connections, onlineUsers) = manager.Counts();
            var list = manager.OnlineUsers(Math.Clamp(limit ?? 100, 1, 500));
            var names = list.Count == 0
                ? new Dictionary<string, string>()
                : (await users.GetPublicAsync(list.Select(x => x.UserId).ToArray(), ct)).ToDictionary(p => p.Id, p => p.Username);

            return Results.Ok(new
            {
                connections,
                users = onlineUsers,
                items = list.Select(x => new { userId = x.UserId, username = names.GetValueOrDefault(x.UserId), connections = x.Connections })
            });
        });

        // POST /admin/api/users/{id}/disconnect : close all realtime connections of a player
        admin.MapPost("/users/{id}/disconnect", async (string id, IRealtime realtime) =>
        {
            await realtime.DisconnectUserAsync(id, "disconnected_by_admin");
            return Results.NoContent();
        });

        return admin;
    }
}

public sealed record AdminChatSend(string? Channel, string? Text);
public sealed record AdminMuteRequest(int? Minutes, string? Reason);

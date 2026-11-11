using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Social;

namespace GameServer.Host.Endpoints;

/// <summary>Friends, groups/clans and parties. Every action runs through the before/after hooks of its HookOps name.</summary>
public static class SocialEndpoints
{
    private static string Me(HttpContext http) => http.User.FindFirst("sub")!.Value;

    public static IEndpointRouteBuilder MapSocial(this IEndpointRouteBuilder app)
    {
        MapFriends(app.MapGroup("/v1/friends").RequireAuthorization());
        MapGroups(app.MapGroup("/v1/groups").RequireAuthorization());
        MapParties(app.MapGroup("/v1/parties").RequireAuthorization());
        return app;
    }

    // ---- friends ------------------------------------------------------------------------------------------

    private static void MapFriends(RouteGroupBuilder g)
    {
        // GET /v1/friends?state=friend|invite_sent|invite_received|blocked
        g.MapGet("/", async (string? state, int? limit, HttpContext http, FriendsService friends, CancellationToken ct) =>
        {
            FriendState? parsed = null;
            if (!string.IsNullOrEmpty(state))
            {
                parsed = state.ToLowerInvariant() switch
                {
                    "friend" => FriendState.Friend,
                    "invite_sent" => FriendState.InviteSent,
                    "invite_received" => FriendState.InviteReceived,
                    "blocked" => FriendState.Blocked,
                    _ => throw new GameException("invalid_request", "state must be friend, invite_sent, invite_received or blocked.")
                };
            }

            return Results.Ok(new { friends = await friends.ListAsync(Me(http), parsed, Math.Clamp(limit ?? 100, 1, 200), ct) });
        });

        // POST /v1/friends   body: { "userId": "..." } or { "username": "..." }
        // Sends an invite, or accepts one that player already sent you.
        g.MapPost("/", async (FriendRequest req, HttpContext http, FriendsService friends, HookRunner hooks, CancellationToken ct) =>
            Results.Ok(await hooks.RunAsync(HookOps.FriendsAdd, Me(http), req, r => friends.AddAsync(Me(http), r, ct), ct)));

        // DELETE /v1/friends/{userId} : remove a friend, or cancel/decline an invite
        g.MapDelete("/{userId}", async (string userId, HttpContext http, FriendsService friends, HookRunner hooks, CancellationToken ct) =>
        {
            var removed = await hooks.RunAsync(HookOps.FriendsRemove, Me(http), new TargetRequest(userId),
                r => friends.RemoveAsync(Me(http), r.UserId, ct), ct);
            return removed ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
        });

        g.MapPost("/{userId}/block", async (string userId, HttpContext http, FriendsService friends, HookRunner hooks, CancellationToken ct) =>
        {
            await hooks.RunVoidAsync(HookOps.FriendsBlock, Me(http), new TargetRequest(userId),
                r => friends.BlockAsync(Me(http), r.UserId, ct), ct);
            return Results.NoContent();
        });

        g.MapDelete("/{userId}/block", async (string userId, HttpContext http, FriendsService friends, HookRunner hooks, CancellationToken ct) =>
        {
            var done = await hooks.RunAsync(HookOps.FriendsUnblock, Me(http), new TargetRequest(userId),
                r => friends.UnblockAsync(Me(http), r.UserId, ct), ct);
            return done ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
        });
    }

    // ---- groups / clans -----------------------------------------------------------------------------------

    private static void MapGroups(RouteGroupBuilder g)
    {
        // POST /v1/groups   body: { name, description, avatar, type: "clan"|"group"|..., open, maxMembers }
        g.MapPost("/", async (CreateGroupRequest req, HttpContext http, GroupService groups, HookRunner hooks, CancellationToken ct) =>
            Results.Ok(new { group = await hooks.RunAsync(HookOps.GroupsCreate, Me(http), req, r => groups.CreateAsync(Me(http), r, ct), ct) }));

        // GET /v1/groups?q=&type=&limit=
        g.MapGet("/", async (string? q, string? type, int? limit, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { groups = await groups.SearchAsync(q, type, Math.Clamp(limit ?? 20, 1, 100), ct) }));

        // GET /v1/groups/mine : groups I belong to (or am invited to / requested)
        g.MapGet("/mine", async (HttpContext http, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { groups = await groups.MineAsync(Me(http), ct) }));

        g.MapGet("/{id}", async (string id, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { group = await groups.GetAsync(id, ct) }));

        // PUT /v1/groups/{id}   body: { name, description, avatar, open, maxMembers } (send only what changes)
        g.MapPut("/{id}", async (string id, GroupUpdateBody body, HttpContext http, GroupService groups, HookRunner hooks, CancellationToken ct) =>
        {
            var req = new GroupUpdateRequest(id, body.Name, body.Description, body.Avatar, body.Open, body.MaxMembers);
            return Results.Ok(new { group = await hooks.RunAsync(HookOps.GroupsUpdate, Me(http), req, r => groups.UpdateAsync(Me(http), r, ct), ct) });
        });

        g.MapDelete("/{id}", async (string id, HttpContext http, GroupService groups, HookRunner hooks, CancellationToken ct) =>
        {
            await hooks.RunVoidAsync(HookOps.GroupsDelete, Me(http), new GroupRequest(id), r => groups.DeleteAsync(Me(http), r.GroupId, ct), ct);
            return Results.NoContent();
        });

        // POST /v1/groups/{id}/join : joins an open group, or sends a join request / accepts an invite
        g.MapPost("/{id}/join", async (string id, HttpContext http, GroupService groups, HookRunner hooks, CancellationToken ct) =>
            Results.Ok(new { status = await hooks.RunAsync(HookOps.GroupsJoin, Me(http), new GroupRequest(id), r => groups.JoinAsync(Me(http), r.GroupId, ct), ct) }));

        g.MapPost("/{id}/leave", async (string id, HttpContext http, GroupService groups, HookRunner hooks, CancellationToken ct) =>
        {
            await hooks.RunVoidAsync(HookOps.GroupsLeave, Me(http), new GroupRequest(id), r => groups.LeaveAsync(Me(http), r.GroupId, ct), ct);
            return Results.NoContent();
        });

        // GET /v1/groups/{id}/members?pending=false&limit=100  (pending=true: join requests and invites, admins only)
        g.MapGet("/{id}/members", async (string id, bool? pending, int? limit, HttpContext http, GroupService groups, CancellationToken ct) =>
            Results.Ok(new { members = await groups.MembersAsync(Me(http), id, pending ?? false, Math.Clamp(limit ?? 100, 1, 200), ct) }));

        Target(g, HttpMethod.Post, "/{id}/invite/{userId}", HookOps.GroupsInvite, (s, a, gid, uid, ct) => s.InviteAsync(a, gid, uid, ct));
        Target(g, HttpMethod.Post, "/{id}/members/{userId}/accept", HookOps.GroupsAccept, (s, a, gid, uid, ct) => s.AcceptAsync(a, gid, uid, ct));
        Target(g, HttpMethod.Post, "/{id}/members/{userId}/kick", HookOps.GroupsKick, (s, a, gid, uid, ct) => s.KickAsync(a, gid, uid, ct));
        Target(g, HttpMethod.Post, "/{id}/members/{userId}/ban", HookOps.GroupsBan, (s, a, gid, uid, ct) => s.BanAsync(a, gid, uid, ct));
        Target(g, HttpMethod.Delete, "/{id}/members/{userId}/ban", HookOps.GroupsUnban, (s, a, gid, uid, ct) => s.UnbanAsync(a, gid, uid, ct));
        Target(g, HttpMethod.Post, "/{id}/members/{userId}/promote", HookOps.GroupsPromote, (s, a, gid, uid, ct) => s.PromoteAsync(a, gid, uid, ct));
        Target(g, HttpMethod.Post, "/{id}/members/{userId}/demote", HookOps.GroupsDemote, (s, a, gid, uid, ct) => s.DemoteAsync(a, gid, uid, ct));
        Target(g, HttpMethod.Post, "/{id}/transfer/{userId}", HookOps.GroupsTransfer, (s, a, gid, uid, ct) => s.TransferAsync(a, gid, uid, ct));
    }

    /// <summary>One endpoint of the shape "do X to player U in group G", wrapped in hooks.</summary>
    private static void Target(
        RouteGroupBuilder g, HttpMethod method, string pattern, string operation,
        Func<GroupService, string, string, string, CancellationToken, Task> action)
    {
        Delegate handler = async (string id, string userId, HttpContext http, GroupService groups, HookRunner hooks, CancellationToken ct) =>
        {
            await hooks.RunVoidAsync(operation, Me(http), new GroupTargetRequest(id, userId),
                r => action(groups, Me(http), r.GroupId, r.UserId, ct), ct);
            return Results.NoContent();
        };

        if (method == HttpMethod.Delete) g.MapDelete(pattern, handler);
        else g.MapPost(pattern, handler);
    }

    // ---- parties ------------------------------------------------------------------------------------------

    private static void MapParties(RouteGroupBuilder g)
    {
        // POST /v1/parties   body: { "open": false, "maxSize": 4 }. Needs an open realtime connection.
        g.MapPost("/", async (CreatePartyRequest? req, HttpContext http, PartyService parties, HookRunner hooks, CancellationToken ct) =>
            Results.Ok(new { party = await hooks.RunAsync(HookOps.PartiesCreate, Me(http), req ?? new CreatePartyRequest(null, null),
                r => parties.CreateAsync(Me(http), r.Open ?? false, r.MaxSize, ct), ct) }));

        g.MapGet("/mine", (HttpContext http, PartyService parties) =>
            Results.Ok(new { party = parties.Mine(Me(http)) }));

        g.MapGet("/{id}", (string id, HttpContext http, PartyService parties) =>
            Results.Ok(new { party = parties.Get(id, Me(http)) }));

        g.MapPost("/{id}/join", async (string id, HttpContext http, PartyService parties, HookRunner hooks, CancellationToken ct) =>
            Results.Ok(new { party = await hooks.RunAsync(HookOps.PartiesJoin, Me(http), new PartyRequest(id),
                r => parties.JoinAsync(Me(http), r.PartyId, ct), ct) }));

        g.MapPost("/leave", async (HttpContext http, PartyService parties, HookRunner hooks, CancellationToken ct) =>
        {
            await hooks.RunVoidAsync(HookOps.PartiesLeave, Me(http), new EmptyRequest(), _ => parties.LeaveAsync(Me(http), ct), ct);
            return Results.NoContent();
        });

        PartyTarget(g, "/invite/{userId}", HookOps.PartiesInvite, (s, a, uid, ct) => s.InviteAsync(a, uid, ct));
        PartyTarget(g, "/kick/{userId}", HookOps.PartiesKick, (s, a, uid, ct) => s.KickAsync(a, uid, ct));
        PartyTarget(g, "/promote/{userId}", HookOps.PartiesPromote, (s, a, uid, ct) => s.PromoteAsync(a, uid, ct));

        // DELETE /v1/parties/mine : the leader closes the party
        g.MapDelete("/mine", async (HttpContext http, PartyService parties, HookRunner hooks, CancellationToken ct) =>
        {
            await hooks.RunVoidAsync(HookOps.PartiesClose, Me(http), new EmptyRequest(), _ => parties.CloseAsync(Me(http), ct), ct);
            return Results.NoContent();
        });
    }

    private static void PartyTarget(
        RouteGroupBuilder g, string pattern, string operation, Func<PartyService, string, string, CancellationToken, Task> action) =>
        g.MapPost(pattern, async (string userId, HttpContext http, PartyService parties, HookRunner hooks, CancellationToken ct) =>
        {
            await hooks.RunVoidAsync(operation, Me(http), new TargetRequest(userId), r => action(parties, Me(http), r.UserId, ct), ct);
            return Results.NoContent();
        });
}

public sealed record GroupUpdateBody(string? Name, string? Description, string? Avatar, bool? Open, int? MaxMembers);

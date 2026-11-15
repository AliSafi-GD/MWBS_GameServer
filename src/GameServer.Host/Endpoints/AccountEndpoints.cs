using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Identity;
using GameServer.Core.Social;

namespace GameServer.Host.Endpoints;

/// <summary>The signed-in player: profile, logout from all devices, linked logins, and other players' public profiles.</summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccount(this IEndpointRouteBuilder app)
    {
        // GET /v1/account : my profile
        app.MapGet("/v1/account", async (HttpContext http, IUserStore users, CancellationToken ct) =>
        {
            var user = await users.GetAsync(http.UserId(), ct);
            if (user is null)
                return Results.NotFound(new { error = "not_found" });
            return Results.Ok(ViewOf(user, await users.ListIdentitiesAsync(user.Id, ct)));
        }).RequireAuthorization();

        // PUT /v1/account : change username / displayName / avatar / lang / timezone (send only what changes)
        app.MapPut("/v1/account", async (ProfileUpdate update, HttpContext http, IUserStore users, HookRunner hooks, CancellationToken ct) =>
        {
            var userId = http.UserId();
            var view = await hooks.RunAsync<ProfileUpdate, AccountView>(HookOps.AccountUpdate, userId, update, async u =>
            {
                var user = await users.UpdateProfileAsync(userId, ProfileValidator.Normalize(u), ct)
                           ?? throw new GameException("not_found", "Account not found.");
                return ViewOf(user, await users.ListIdentitiesAsync(user.Id, ct));
            }, ct);
            return Results.Ok(view);
        }).RequireAuthorization();

        // DELETE /v1/account : delete my account and everything stored for it (required by app stores)
        app.MapDelete("/v1/account", async (HttpContext http, IUserStore users, AccountCleanup cleanup, HookRunner hooks, CancellationToken ct) =>
        {
            var userId = http.UserId();
            await hooks.RunVoidAsync(HookOps.AccountDelete, userId, new EmptyRequest(), async _ =>
            {
                await cleanup.BeforeDeleteAsync(userId, ct);
                await users.DeleteAsync(userId, ct);
            }, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // POST /v1/account/logout-all : end every session of this account (all devices)
        app.MapPost("/v1/account/logout-all", async (HttpContext http, IRefreshTokenStore refresh, CancellationToken ct) =>
        {
            await refresh.RevokeAllAsync(http.UserId(), ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // POST /v1/account/link/{provider}   body: { "credential": { ... } }
        // Attaches a login (e.g. Google Play) to the signed-in account so progress survives a lost device.
        // 409 identity_in_use: that login already belongs to another account. 409 provider_already_linked: this account
        // already has a login of that type.
        app.MapPost("/v1/account/link/{provider}", async (string provider, ProviderLinkRequest req, HttpContext http, IdentityProviders providers, IUserStore users, CancellationToken ct) =>
        {
            var identity = await providers.Get(provider).VerifyAsync(req.Credential, ct);
            var result = await users.LinkIdentityAsync(http.UserId(), identity.Provider, identity.Subject, identity.Label, ct);

            return result switch
            {
                LinkResult.Linked or LinkResult.AlreadyLinked => Results.Ok(new { identities = await users.ListIdentitiesAsync(http.UserId(), ct) }),
                LinkResult.InUseByOtherAccount => throw new GameException("identity_in_use", "This login already belongs to another account."),
                _ => throw new GameException("provider_already_linked", "This account already has a login of that type.")
            };
        }).RequireAuthorization().RequireRateLimiting("auth");

        // DELETE /v1/account/link/{provider} : remove a login (the last remaining login method cannot be removed)
        app.MapDelete("/v1/account/link/{provider}", async (string provider, HttpContext http, IUserStore users, CancellationToken ct) =>
        {
            var result = await users.UnlinkIdentityAsync(http.UserId(), provider, ct);
            return result switch
            {
                UnlinkResult.Unlinked => Results.NoContent(),
                UnlinkResult.NotLinked => throw new GameException("not_found", "That login is not linked to this account."),
                _ => throw new GameException("last_login_method", "You cannot remove your only way to log in.")
            };
        }).RequireAuthorization();

        // GET /v1/users?ids=a,b,c : public profiles of other players (max 100 ids)
        app.MapGet("/v1/users", async (string ids, IUserStore users, CancellationToken ct) =>
        {
            var list = ids
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .Take(100)
                .ToArray();
            return Results.Ok(new { users = await users.GetPublicAsync(list, ct) });
        }).RequireAuthorization();

        return app;
    }

    // What a player sees about their own account (no admin-only fields).
    private static AccountView ViewOf(User u, IReadOnlyList<IdentityInfo> identities) =>
        new(u.Id, u.Username, u.DisplayName, u.Avatar, u.Lang, u.Timezone, u.CreatedUtc, u.LastLoginUtc, identities);
}

/// <summary>What a player sees about their own account (no admin-only fields).</summary>
public sealed record AccountView(
    string Id, string Username, string DisplayName, string Avatar, string Lang, string Timezone,
    DateTime CreatedUtc, DateTime? LastLoginUtc, IReadOnlyList<IdentityInfo> Identities);

public sealed record ProviderLinkRequest(JsonElement Credential);

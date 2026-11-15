using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Identity;

namespace GameServer.Host.Endpoints;

/// <summary>Login (device id and the other identity providers), token refresh and logout.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        // POST /v1/auth/device   body: { "deviceId": "...", "username": "optional", "create": true }
        // Creates the account on first call (unless create=false) and returns a JWT.
        // 404 unknown_provider when "device" is not in GameServer:Auth:Providers (same answer as any switched-off login).
        app.MapPost("/v1/auth/device", async (DeviceAuthRequest req, IdentityProviders providers, IIdentityStore users, TokenService tokens, IRefreshTokenStore refresh, HookRunner hooks, TimeProvider time, CancellationToken ct) =>
        {
            providers.Get(AuthProviderSelection.Device);

            if (string.IsNullOrWhiteSpace(req.DeviceId) || req.DeviceId.Length is < 16 or > 128)
                return Results.BadRequest(new { error = "invalid_device_id", message = "deviceId must be 16-128 characters." });

            var username = req.Username is null ? null : ProfileValidator.NormalizeUsername(req.Username);

            var user = await users.LoginDeviceAsync(req.DeviceId, req.Create, username, ct);
            if (user is null)
                return Results.Json(new { error = "account_not_found" }, statusCode: StatusCodes.Status404NotFound);
            if (user.Disabled)
                return Results.Json(new { error = "account_disabled" }, statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(await LoginResultAsync(user, "device", tokens, refresh, hooks, time, ct));
        }).RequireRateLimiting("device");

        // GET /v1/auth/providers : which login methods are switched on, including "device" when guest login is on
        app.MapGet("/v1/auth/providers", (IdentityProviders providers) => Results.Ok(new { providers = providers.EnabledNames }));

        // POST /v1/auth/{provider}/begin   body: { "credential": { ... } }
        // First step for providers that need one (phone: sends the code). 204 on success.
        app.MapPost("/v1/auth/{provider}/begin", async (string provider, ProviderBeginRequest req, IdentityProviders providers, CancellationToken ct) =>
        {
            await providers.Get(provider).BeginAsync(req.Credential, ct);
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        // POST /v1/auth/refresh   body: { "refreshToken": "..." }
        // Trades a refresh token for a new access token AND a new refresh token (the old one stops working).
        // 401 invalid_refresh_token => the session is over (expired, logged out, or the token was reused): log in again.
        app.MapPost("/v1/auth/refresh", async (RefreshRequest req, IRefreshTokenStore refresh, IAccountStore users, TokenService tokens, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RefreshToken) || req.RefreshToken.Length > 256)
                throw new GameException("invalid_refresh_token", "The refresh token is not valid. Log in again.");

            var (userId, next) = await refresh.RotateAsync(req.RefreshToken, ct);

            var user = await users.GetAsync(userId, ct);
            if (user is null || user.Disabled)
            {
                await refresh.RevokeAllAsync(userId, ct);
                return Results.Json(new { error = "account_disabled" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var (token, expiresUtc) = tokens.Issue(user.Id);
            return Results.Ok(new { token, refreshToken = next, userId = user.Id, username = user.Username, expiresUtc });
        }).RequireRateLimiting("auth");

        // POST /v1/auth/logout   body: { "refreshToken": "..." }
        // Ends this device's session. Always 204 (it does not reveal whether the token existed). The access token
        // simply expires on its own (<= ExpiryMinutes).
        app.MapPost("/v1/auth/logout", async (RefreshRequest req, IRefreshTokenStore refresh, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(req.RefreshToken) && req.RefreshToken.Length <= 256)
                await refresh.RevokeSessionAsync(req.RefreshToken, ct);
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        // POST /v1/auth/{provider}   body: { "credential": { ... }, "create": true, "username": "optional" }
        // Logs in with a verified identity. If no account owns it yet, one is created (unless create=false, which is
        // how a player restores their account on a new phone).
        app.MapPost("/v1/auth/{provider}", async (string provider, ProviderAuthRequest req, IdentityProviders providers, IIdentityStore users, TokenService tokens, IRefreshTokenStore refresh, HookRunner hooks, TimeProvider time, CancellationToken ct) =>
        {
            var identity = await providers.Get(provider).VerifyAsync(req.Credential, ct);

            var user = await users.LoginWithIdentityAsync(identity.Provider, identity.Subject, ct);
            if (user is null)
            {
                if (!req.Create)
                    return Results.Json(new { error = "account_not_found" }, statusCode: StatusCodes.Status404NotFound);

                var username = req.Username is null ? null : ProfileValidator.NormalizeUsername(req.Username);
                user = await users.CreateWithIdentityAsync(identity.Provider, identity.Subject, identity.Label, username, ct);
            }

            if (user is null)
                return Results.Json(new { error = "account_not_found" }, statusCode: StatusCodes.Status404NotFound);
            if (user.Disabled)
                return Results.Json(new { error = "account_disabled" }, statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(await LoginResultAsync(user, provider, tokens, refresh, hooks, time, ct));
        }).RequireRateLimiting("auth");

        return app;
    }

    private static async Task<LoginResult> LoginResultAsync(
        User user, string provider, TokenService tokens, IRefreshTokenStore refresh, HookRunner hooks, TimeProvider time, CancellationToken ct)
    {
        var (token, expiresUtc) = tokens.Issue(user.Id);
        var refreshToken = await refresh.IssueAsync(user.Id, ct);
        var result = new LoginResult(token, refreshToken, user.Id, user.Username, expiresUtc);

        // After-hooks only (a login cannot be blocked this way). "IsNew" is a guess based on the account age, so
        // modules should still use idempotency keys for one-time rewards.
        var isNew = time.UtcNow() - user.CreatedUtc < TimeSpan.FromSeconds(5);
        await hooks.NotifyAsync("auth.login", user.Id, new LoginEvent(provider, isNew), result, ct);
        return result;
    }
}

// <summary>What every login answers: the access token (JWT), the refresh token and who signed in.</summary>
public sealed record LoginResult(string Token, string RefreshToken, string UserId, string Username, DateTime ExpiresUtc);

public sealed record DeviceAuthRequest(string DeviceId, string? Username = null, bool Create = true);
public sealed record ProviderAuthRequest(JsonElement Credential, string? Username = null, bool Create = true);
public sealed record ProviderBeginRequest(JsonElement Credential);
public sealed record RefreshRequest(string RefreshToken);

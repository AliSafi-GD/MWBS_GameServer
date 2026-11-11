using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Text;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Admin;
using GameServer.Core.Identity;
using GameServer.Core.Matches;
using GameServer.Core.Progression;
using GameServer.Core.Shop;
using GameServer.Core.Realtime;
using GameServer.Core.Social;
using GameServer.Host.Endpoints;
using GameServer.Transports.Tcp;
using GameServer.Transports.Udp;
using GameServer.Transports.Ws;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("GameServer"));

var jwtSection = builder.Configuration.GetSection("GameServer:Jwt");
var jwt = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
jwt.EnsureValid(); // fails fast at startup if the secret is missing or too short
builder.Services.Configure<JwtOptions>(jwtSection);

var adminSection = builder.Configuration.GetSection("GameServer:Admin");
var adminOptions = adminSection.Get<AdminOptions>() ?? new AdminOptions();
adminOptions.EnsureValid();
builder.Services.Configure<AdminOptions>(adminSection);

// Chat/realtime transports are chosen when the server is built. Default: config GameServer:Realtime:Transports
// (env GameServer__Realtime__Transports, e.g. "websocket" or "websocket,tcp"). To pin them in code instead:
//   builder.Services.AddGameServerCore(builder.Configuration, builder.Environment.IsDevelopment(),
//       realtime: rt => rt.Use("websocket", "tcp"));
// A custom protocol: rt.Add<MyTransport>("mine") with MyTransport : ITransport, then rt.Use("mine").
// Match/matchmaker protocols work the same way: GameServer__Match__Transports (e.g. "websocket,udp") switches them on,
// and each mode in the "matches" config picks one with "transport". AddLiteNetLib() only makes "udp" available.
// Your own: mt.Add<MyTransport>("quic") with MyTransport : IMatchTransport.
builder.Services.AddGameServerCore(builder.Configuration, builder.Environment.IsDevelopment(),
    realtime: rt => rt.AddWebSocket().AddTcp(),
    match: mt => mt.AddWebSocket().AddLiteNetLib());

// Basic abuse protection, counted per client IP. Behind a reverse proxy (nginx, Caddy...) enable forwarded
// headers so the real client IP is used instead of the proxy's.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    // Every client reads config at startup and many players share one IP (mobile networks), so be generous.
    options.AddPolicy("config", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    // The admin panel's dashboard refreshes by itself, so allow more than for players. Keys are long random strings,
    // so guessing them is not a realistic risk at this rate.
    options.AddPolicy("admin", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep "sub" as "sub"
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Issuer,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // Tokens are stateless, so on every request make sure the account still exists and is not banned.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userId = context.Principal?.FindFirst("sub")?.Value;
                var users = context.HttpContext.RequestServices.GetRequiredService<IUserStore>();
                var user = userId is null ? null : await users.GetAsync(userId, context.HttpContext.RequestAborted);
                if (user is null || user.Disabled)
                    context.Fail("account_unavailable");
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Load game modules once at startup (they register their RPCs into ModuleRegistry).
app.Services.GetRequiredService<ModuleLoader>().LoadAll();

// Create/upgrade database tables.
await app.Services.GetRequiredService<GameServer.Core.Postgres.DatabaseMigrator>().MigrateAsync();

// Turn GameException (thrown by validation, stores and modules) into clean JSON errors.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (GameException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusFor(ex.Code);
        await context.Response.WriteAsJsonAsync(new { error = ex.Code, message = ex.Message });
    }
});

// Maintenance mode: while config key "maintenance" has enabled=true, the game API answers 503.
// Config, version check, health and the admin API stay reachable so clients can show the message.
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

app.UseDefaultFiles();
app.UseStaticFiles(); // serves the admin panel from wwwroot/admin
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseGameServerRealtime(); // websocket / tcp / ... endpoints, as chosen at build time
app.UseGameServerMatches(); // match transports (websocket, udp...), as chosen at build time

app.MapGet("/healthz", (ModuleRegistry registry) => Results.Ok(new { status = "ok", rpcs = registry.RpcIds }));

// ---------------------------------------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------------------------------------

// POST /v1/auth/device   body: { "deviceId": "...", "username": "optional", "create": true }
// Creates the account on first call (unless create=false) and returns a JWT.
app.MapPost("/v1/auth/device", async (DeviceAuthRequest req, IUserStore users, TokenService tokens, IRefreshTokenStore refresh, HookRunner hooks, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.DeviceId) || req.DeviceId.Length is < 16 or > 128)
        return Results.BadRequest(new { error = "invalid_device_id", message = "deviceId must be 16-128 characters." });

    var username = req.Username is null ? null : ProfileValidator.NormalizeUsername(req.Username);

    var user = await users.LoginDeviceAsync(req.DeviceId, req.Create, username, ct);
    if (user is null)
        return Results.Json(new { error = "account_not_found" }, statusCode: StatusCodes.Status404NotFound);
    if (user.Disabled)
        return Results.Json(new { error = "account_disabled" }, statusCode: StatusCodes.Status403Forbidden);

    return Results.Ok(await LoginResultAsync(user, "device", tokens, refresh, hooks, ct));
});

// GET /v1/auth/providers : which login methods this server offers (besides the device id)
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
app.MapPost("/v1/auth/refresh", async (RefreshRequest req, IRefreshTokenStore refresh, IUserStore users, TokenService tokens, CancellationToken ct) =>
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
app.MapPost("/v1/auth/{provider}", async (string provider, ProviderAuthRequest req, IdentityProviders providers, IUserStore users, TokenService tokens, IRefreshTokenStore refresh, HookRunner hooks, CancellationToken ct) =>
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

    return Results.Ok(await LoginResultAsync(user, provider, tokens, refresh, hooks, ct));
}).RequireRateLimiting("auth");

// ---------------------------------------------------------------------------------------------------------
// Remote config and version check (no login needed: clients ask before they log in)
// ---------------------------------------------------------------------------------------------------------

// GET /v1/config : all public config keys as { "config": { key: value } }.
// Supports ETag: send If-None-Match and get 304 when nothing changed.
app.MapGet("/v1/config", async (HttpContext http, IConfigStore config, CancellationToken ct) =>
{
    var values = (await config.ListAsync(ct)).Where(e => e.IsPublic).ToDictionary(e => e.Key, e => e.Value);
    var json = JsonSerializer.SerializeToUtf8Bytes(new { config = values });
    var etag = "\"" + Convert.ToHexString(SHA256.HashData(json))[..16] + "\"";

    http.Response.Headers.ETag = etag;
    http.Response.Headers.CacheControl = "no-cache";
    if (http.Request.Headers["If-None-Match"].ToString() == etag)
        return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Bytes(json, "application/json");
}).RequireRateLimiting("config");

// GET /v1/version-check?store=bazaar&version=1.2.0
// Compares the client's version with config key "app_version" ({ "<store>": { latest, minimum, url }, "default": {...} }).
// status: "ok" | "update_available" (older than latest) | "update_required" (older than minimum).
app.MapGet("/v1/version-check", async (string? store, string version, IConfigStore config, CancellationToken ct) =>
{
    var client = ParseVersion(version)
                 ?? throw new GameException("invalid_version", "version must look like 1.2 or 1.2.3.");

    var root = (await config.GetAsync(ConfigRules.AppVersion, ct))?.Value;
    JsonElement? rule = null;
    if (root is { ValueKind: JsonValueKind.Object } r)
    {
        if (!string.IsNullOrEmpty(store) && r.TryGetProperty(store, out var byStore) && byStore.ValueKind == JsonValueKind.Object)
            rule = byStore;
        else if (r.TryGetProperty("default", out var fallback) && fallback.ValueKind == JsonValueKind.Object)
            rule = fallback;
    }

    string? Get(string name) =>
        rule is { } x && x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    var latest = Get("latest");
    var minimum = Get("minimum");

    var status = "ok";
    if (ParseVersion(minimum) is { } min && client < min) status = "update_required";
    else if (ParseVersion(latest) is { } lat && client < lat) status = "update_available";

    return Results.Ok(new { status, latest, minimum, url = Get("url") });
}).RequireRateLimiting("config");

// ---------------------------------------------------------------------------------------------------------
// Account (the signed-in player)
// ---------------------------------------------------------------------------------------------------------

// GET /v1/account : my profile
app.MapGet("/v1/account", async (HttpContext http, IUserStore users, CancellationToken ct) =>
{
    var user = await users.GetAsync(CurrentUserId(http), ct);
    if (user is null)
        return Results.NotFound(new { error = "not_found" });
    return Results.Ok(AccountView(user, await users.ListIdentitiesAsync(user.Id, ct)));
}).RequireAuthorization();

// PUT /v1/account : change username / displayName / avatar / lang / timezone (send only what changes)
app.MapPut("/v1/account", async (ProfileUpdate update, HttpContext http, IUserStore users, HookRunner hooks, CancellationToken ct) =>
{
    var userId = CurrentUserId(http);
    var view = await hooks.RunAsync<ProfileUpdate, object>(HookOps.AccountUpdate, userId, update, async u =>
    {
        var user = await users.UpdateProfileAsync(userId, ProfileValidator.Normalize(u), ct)
                   ?? throw new GameException("not_found", "Account not found.");
        return AccountView(user, await users.ListIdentitiesAsync(user.Id, ct));
    }, ct);
    return Results.Ok(view);
}).RequireAuthorization();

// DELETE /v1/account : delete my account and everything stored for it (required by app stores)
app.MapDelete("/v1/account", async (HttpContext http, IUserStore users, AccountCleanup cleanup, HookRunner hooks, CancellationToken ct) =>
{
    var userId = CurrentUserId(http);
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
    await refresh.RevokeAllAsync(CurrentUserId(http), ct);
    return Results.NoContent();
}).RequireAuthorization();

// POST /v1/account/link/{provider}   body: { "credential": { ... } }
// Attaches a login (e.g. Google Play) to the signed-in account so progress survives a lost device.
// 409 identity_in_use: that login already belongs to another account. 409 provider_already_linked: this account
// already has a login of that type.
app.MapPost("/v1/account/link/{provider}", async (string provider, ProviderLinkRequest req, HttpContext http, IdentityProviders providers, IUserStore users, CancellationToken ct) =>
{
    var identity = await providers.Get(provider).VerifyAsync(req.Credential, ct);
    var result = await users.LinkIdentityAsync(CurrentUserId(http), identity.Provider, identity.Subject, identity.Label, ct);

    return result switch
    {
        LinkResult.Linked or LinkResult.AlreadyLinked => Results.Ok(new { identities = await users.ListIdentitiesAsync(CurrentUserId(http), ct) }),
        LinkResult.InUseByOtherAccount => throw new GameException("identity_in_use", "This login already belongs to another account."),
        _ => throw new GameException("provider_already_linked", "This account already has a login of that type.")
    };
}).RequireAuthorization().RequireRateLimiting("auth");

// DELETE /v1/account/link/{provider} : remove a login (the last remaining login method cannot be removed)
app.MapDelete("/v1/account/link/{provider}", async (string provider, HttpContext http, IUserStore users, CancellationToken ct) =>
{
    var result = await users.UnlinkIdentityAsync(CurrentUserId(http), provider, ct);
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

// GET /v1/wallet : my currencies, e.g. { "wallet": { "coins": 500, "gems": 3 } } (read-only: only modules change balances)
app.MapGet("/v1/wallet", async (HttpContext http, IEconomy economy, CancellationToken ct) =>
    Results.Ok(new { wallet = (await economy.GetAsync(CurrentUserId(http), ct)).Wallet })).RequireAuthorization();

// GET /v1/inventory : my items, e.g. { "items": { "card_fire": 2 } }
app.MapGet("/v1/inventory", async (HttpContext http, IEconomy economy, CancellationToken ct) =>
    Results.Ok(new { items = (await economy.GetAsync(CurrentUserId(http), ct)).Items })).RequireAuthorization();

// ---------------------------------------------------------------------------------------------------------
// Leaderboards and tournaments (read-only for clients: modules submit scores)
// ---------------------------------------------------------------------------------------------------------

// GET /v1/leaderboards/{id}?limit=50&around=0&period=current
// Top entries plus "me" (my rank and score). around=N returns N entries above and below me instead of the top.
// period: current (default), all, 2026-11-08, 2026-W45 or 2026-11 to read an earlier day/week/month.
app.MapGet("/v1/leaderboards/{id}", async (string id, string? period, int? limit, int? around, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
    Results.Ok(await competitions.GetBoardAsync(id, period, limit ?? 50, around ?? 0, CurrentUserId(http), ct))).RequireAuthorization();

// GET /v1/tournaments : upcoming, active and recently ended tournaments, with whether I joined / claimed
app.MapGet("/v1/tournaments", async (HttpContext http, ICompetitions competitions, CancellationToken ct) =>
    Results.Ok(new { tournaments = await competitions.ListTournamentsAsync(CurrentUserId(http), ct) })).RequireAuthorization();

// GET /v1/tournaments/{id}?limit=50&around=0 : the tournament and its standings
app.MapGet("/v1/tournaments/{id}", async (string id, int? limit, int? around, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
{
    var userId = CurrentUserId(http);
    var board = await competitions.GetBoardAsync("tournament:" + id, null, limit ?? 50, around ?? 0, userId, ct);
    var info = (await competitions.ListTournamentsAsync(userId, ct)).FirstOrDefault(t => t.Id == id);
    return Results.Ok(new { tournament = info, board });
}).RequireAuthorization();

// POST /v1/tournaments/{id}/join : pays the entry fee (once) and joins. 409 insufficient_funds / tournament_not_active
app.MapPost("/v1/tournaments/{id}/join", async (string id, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
    Results.Ok(new { tournament = await competitions.JoinTournamentAsync(id, CurrentUserId(http), ct) })).RequireAuthorization();

// POST /v1/tournaments/{id}/claim : after the end, receive the reward for my final rank (once)
app.MapPost("/v1/tournaments/{id}/claim", async (string id, HttpContext http, ICompetitions competitions, CancellationToken ct) =>
    Results.Ok(await competitions.ClaimTournamentAsync(id, CurrentUserId(http), ct))).RequireAuthorization();

// ---------------------------------------------------------------------------------------------------------
// Notifications (my inbox)
// ---------------------------------------------------------------------------------------------------------

// GET /v1/notifications?limit=50&unread=false
app.MapGet("/v1/notifications", async (int? limit, bool? unread, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
    Results.Ok(new { notifications = await notifications.ListAsync(CurrentUserId(http), limit ?? 50, unread ?? false, false, ct) }))
    .RequireAuthorization();

app.MapPost("/v1/notifications/{id:long}/read", async (long id, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
    await notifications.MarkReadAsync(CurrentUserId(http), id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }))
    .RequireAuthorization();

// POST /v1/notifications/{id}/claim : receive the attached reward (once). Returns the reward and my new balances.
app.MapPost("/v1/notifications/{id:long}/claim", async (long id, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
{
    var claim = await notifications.ClaimAsync(CurrentUserId(http), id, ct);
    return Results.Ok(new { reward = claim.Reward, wallet = claim.State.Wallet, items = claim.State.Items });
}).RequireAuthorization();

app.MapDelete("/v1/notifications/{id:long}", async (long id, HttpContext http, INotificationStore notifications, CancellationToken ct) =>
    await notifications.DeleteAsync(CurrentUserId(http), id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }))
    .RequireAuthorization();

// ---------------------------------------------------------------------------------------------------------
// RPC (game modules)
// ---------------------------------------------------------------------------------------------------------

// POST /v1/rpc/{id}   header: Authorization: Bearer <token>   body: any string (usually JSON)
app.MapPost("/v1/rpc/{id}", async (string id, HttpContext http, ModuleRegistry registry, HookRunner hooks, IStorage storage, IConfig config, IEconomy economy, ILeaderboards leaderboards, INotifications notifications, ISocial social, IChat chat, IMatches matches, IProgress progress) =>
{
    if (!registry.TryGetRpc(id, out var handler))
        return Results.NotFound(new { error = "rpc_not_found" });

    string payload;
    using (var reader = new StreamReader(http.Request.Body))
        payload = await reader.ReadToEndAsync();

    var userId = CurrentUserId(http);
    var result = await hooks.RunRawAsync(HookOps.Rpc(id), userId, payload,
        p => handler(new RpcContext(userId, storage, config, economy, leaderboards, notifications, social, chat, matches, progress, http.RequestAborted), p),
        http.RequestAborted);
    return Results.Text(result, "application/json");
}).RequireAuthorization();

// ---------------------------------------------------------------------------------------------------------
// Admin API (used by the panel at /admin). Protected by the X-Admin-Key header.
// ---------------------------------------------------------------------------------------------------------

// Audit log: remember the start of the body of every admin write, run the request, then store who did what and the result.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    if (!context.Request.Path.StartsWithSegments("/admin/api") || method is "GET" or "HEAD" or "OPTIONS")
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
        status = StatusFor(ex.Code);
        throw;
    }
    finally
    {
        if (context.Items[AdminConsoleEndpoints.ActorItem] is AdminActor actor)
        {
            try
            {
                await context.RequestServices.GetRequiredService<AdminStore>().AuditAsync(
                    actor, method, context.Request.Path.Value!["/admin/api".Length..], body, status,
                    context.Connection.RemoteIpAddress?.ToString());
            }
            catch (Exception ex)
            {
                context.RequestServices.GetRequiredService<ILogger<AdminStore>>().LogWarning(ex, "Could not write the admin audit log.");
            }
        }
    }
});

var admin = app.MapGroup("/admin/api").RequireRateLimiting("admin").AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var configured = http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.Key;
    if (string.IsNullOrEmpty(configured))
        return Results.Json(new { error = "admin_disabled", message = "Set GameServer__Admin__Key to enable the admin API." },
            statusCode: StatusCodes.Status404NotFound);

    var given = http.Request.Headers["X-Admin-Key"].ToString();
    var actor = await http.RequestServices.GetRequiredService<AdminStore>().AuthenticateAsync(given, configured, http.RequestAborted);
    if (actor is null)
        return Results.Json(new { error = "forbidden", message = "Wrong admin key." }, statusCode: StatusCodes.Status403Forbidden);

    http.Items[AdminConsoleEndpoints.ActorItem] = actor; // also set when denied below, so the attempt is audited
    var relative = http.Request.Path.Value!["/admin/api".Length..];
    if (!AdminPolicy.IsAllowed(actor.Role, http.Request.Method, relative))
        return Results.Json(new { error = "insufficient_role", message = $"Role {actor.Role} cannot do {http.Request.Method} {relative}." },
            statusCode: StatusCodes.Status403Forbidden);

    return await next(context);
});

// GET /admin/api/users?q=&limit=&offset=
admin.MapGet("/users", async (string? q, int? limit, int? offset, IUserStore users, CancellationToken ct) =>
    Results.Ok(await users.SearchAsync(q, Math.Clamp(limit ?? 25, 1, 100), Math.Max(offset ?? 0, 0), ct)));

// GET /admin/api/users/{id} : profile + logins (masked) + storage
admin.MapGet("/users/{id}", async (string id, IUserStore users, CancellationToken ct) =>
{
    var export = await users.ExportAsync(id, ct);
    return export is null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(export);
});

// GET /admin/api/users/{id}/export : same data as a downloadable file (for privacy requests)
admin.MapGet("/users/{id}/export", async (string id, HttpContext http, IUserStore users, IEconomyStore economy, CancellationToken ct) =>
{
    var export = await users.ExportAsync(id, ct);
    if (export is null)
        return Results.NotFound(new { error = "not_found" });

    http.Response.Headers.ContentDisposition = $"attachment; filename=\"user-{id}.json\"";
    return Results.Ok(new { export.User, export.Devices, export.Identities, export.Storage, economy = await economy.GetAsync(id, ct) });
});

// PUT /admin/api/users/{id} : edit profile fields
admin.MapPut("/users/{id}", async (string id, ProfileUpdate update, IUserStore users, CancellationToken ct) =>
{
    var user = await users.UpdateProfileAsync(id, ProfileValidator.Normalize(update), ct);
    return user is null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(user);
});

admin.MapPost("/users/{id}/ban", async (string id, IUserStore users, IRealtime realtime, CancellationToken ct) =>
{
    if (!await users.SetDisabledAsync(id, true, ct))
        return Results.NotFound(new { error = "not_found" });
    await realtime.DisconnectUserAsync(id, "banned"); // close open chat connections right away
    return Results.NoContent();
});

admin.MapPost("/users/{id}/unban", async (string id, IUserStore users, CancellationToken ct) =>
    await users.SetDisabledAsync(id, false, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

// ---- leaderboards ----

// GET /admin/api/leaderboards/{id}?period=&limit=  (id "tournament:<id>" reads a tournament)
admin.MapGet("/leaderboards/{id}", async (string id, string? period, int? limit, ICompetitions competitions, CancellationToken ct) =>
    Results.Ok(await competitions.GetBoardAsync(id, period, limit ?? 50, 0, null, ct)));

// DELETE /admin/api/leaderboards/{id}/scores/{userId} : remove a player's scores (all periods), e.g. a cheater
admin.MapDelete("/leaderboards/{id}/scores/{userId}", async (string id, string userId, ICompetitions competitions, CancellationToken ct) =>
    await competitions.DeleteScoreAsync(id, userId, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" }));

// ---- notifications ----

// GET /admin/api/users/{id}/notifications : a player's inbox, including expired ones (for support)
admin.MapGet("/users/{id}/notifications", async (string id, int? limit, INotificationStore notifications, CancellationToken ct) =>
    Results.Ok(new { notifications = await notifications.ListAsync(id, limit ?? 50, false, true, ct) }));

// POST /admin/api/users/{id}/notifications
//   body: { "subject": "...", "body": "...", "reward": { "wallet": { "coins": 100 }, "items": {} }, "expiresInDays": 30 }
admin.MapPost("/users/{id}/notifications", async (string id, AdminNotificationRequest req, INotificationStore notifications, CancellationToken ct) =>
    Results.Ok(new { id = await notifications.SendAsync(id, req.Subject ?? "", req.Body, req.Reward, ExpiryFrom(req.ExpiresInDays), ct) }));

// POST /admin/api/notifications/broadcast : same body, sent to every active player
admin.MapPost("/notifications/broadcast", async (AdminNotificationRequest req, INotificationStore notifications, CancellationToken ct) =>
    Results.Ok(new { sent = await notifications.BroadcastAsync(req.Subject ?? "", req.Body, req.Reward, ExpiryFrom(req.ExpiresInDays), ct) }));

// ---- wallet and inventory ----

// GET /admin/api/users/{id}/economy : balances and items
admin.MapGet("/users/{id}/economy", async (string id, IEconomyStore economy, CancellationToken ct) =>
    Results.Ok(await economy.GetAsync(id, ct)));

// GET /admin/api/users/{id}/ledger?limit=50&before=<id> : history, newest first
admin.MapGet("/users/{id}/ledger", async (string id, int? limit, long? before, IEconomyStore economy, CancellationToken ct) =>
    Results.Ok(new { items = await economy.LedgerAsync(id, Math.Clamp(limit ?? 50, 1, 200), before, ct) }));

// POST /admin/api/users/{id}/economy   body: { "wallet": { "coins": 100 }, "items": { "card_fire": -1 }, "reason": "compensation" }
// Negative numbers take away. Saved in the history as "admin: <reason>". Fails with 409 if a balance would go below 0.
admin.MapPost("/users/{id}/economy", async (string id, EconomyAdjustRequest req, IEconomyStore economy, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Reason))
        throw new GameException("invalid_transaction", "A reason is required.");

    var tx = new EconomyTransaction("admin: " + req.Reason.Trim());
    foreach (var (name, delta) in req.Wallet ?? new()) tx.Currency(name, delta);
    foreach (var (name, delta) in req.Items ?? new()) tx.Item(name, delta);

    return Results.Ok((await economy.ApplyAsync(id, tx, ct)).State);
});

// ---- remote config ----

// GET /admin/api/config : every key (public and server-only)
admin.MapGet("/config", async (IConfigStore config, CancellationToken ct) =>
    Results.Ok(new { items = await config.ListAsync(ct) }));

// PUT /admin/api/config/{key}   body: { "value": <any JSON>, "public": true|false }
admin.MapPut("/config/{key}", async (string key, ConfigWriteRequest req, HttpContext http, IConfigStore config, AdminStore adminStore, CancellationToken ct) =>
{
    ConfigRules.ValidateKey(key);
    ConfigRules.ValidateValue(key, req.Value);
    var isPublic = req.Public || ConfigRules.IsAlwaysPublic(key);
    var previous = await config.GetAsync(key, ct);
    if (previous is not null && (previous.IsPublic != isPublic || previous.Value.GetRawText() != req.Value.GetRawText()))
        await adminStore.AddConfigHistoryAsync(previous, AdminConsoleEndpoints.Actor(http).Name, ct);
    return Results.Ok(await config.SetAsync(key, req.Value, isPublic, ct));
});

admin.MapDelete("/config/{key}", async (string key, HttpContext http, IConfigStore config, AdminStore adminStore, CancellationToken ct) =>
{
    var previous = await config.GetAsync(key, ct);
    if (previous is not null)
        await adminStore.AddConfigHistoryAsync(previous, AdminConsoleEndpoints.Actor(http).Name + " (delete)", ct);
    return await config.DeleteAsync(key, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
});

// groups/clans, friends, chat moderation, online players
admin.MapAdminSocial();
admin.MapAdminMatches();
admin.MapAdminShop();
admin.MapAdminProgress();
admin.MapAdminConsole();

admin.MapDelete("/users/{id}", async (string id, IUserStore users, AccountCleanup cleanup, CancellationToken ct) =>
{
    await cleanup.BeforeDeleteAsync(id, ct);
    return await users.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
});

// ---------------------------------------------------------------------------------------------------------
// Friends, groups/clans, parties, chat
// ---------------------------------------------------------------------------------------------------------
app.MapSocial();
app.MapChat();
app.MapMatches();
app.MapShop();
app.MapProgress();

app.Run();

// ---------------------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------------------

// "1.2" and "1.2.0" must compare equal, so missing parts become 0.
static Version? ParseVersion(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;
    text = text.Trim();
    if (!text.Contains('.')) text += ".0";
    return Version.TryParse(text, out var v)
        ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0))
        : null;
}

static DateTime? ExpiryFrom(int? days) => days is > 0 ? DateTime.UtcNow.AddDays(Math.Min(days.Value, 3650)) : null;

static string CurrentUserId(HttpContext http) => http.User.FindFirst("sub")!.Value;

static async Task<object> LoginResultAsync(
    User user, string provider, TokenService tokens, IRefreshTokenStore refresh, HookRunner hooks, CancellationToken ct)
{
    var (token, expiresUtc) = tokens.Issue(user.Id);
    var refreshToken = await refresh.IssueAsync(user.Id, ct);
    var result = new { token, refreshToken, userId = user.Id, username = user.Username, expiresUtc };

    // After-hooks only (a login cannot be blocked this way). "IsNew" is a guess based on the account age, so
    // modules should still use idempotency keys for one-time rewards.
    var isNew = DateTime.UtcNow - user.CreatedUtc < TimeSpan.FromSeconds(5);
    await hooks.NotifyAsync("auth.login", user.Id, new LoginEvent(provider, isNew), result, ct);
    return result;
}

// What a player sees about their own account (no admin-only fields).
static object AccountView(User u, IReadOnlyList<IdentityInfo> identities) => new
{
    id = u.Id,
    username = u.Username,
    displayName = u.DisplayName,
    avatar = u.Avatar,
    lang = u.Lang,
    timezone = u.Timezone,
    createdUtc = u.CreatedUtc,
    lastLoginUtc = u.LastLoginUtc,
    identities = identities.Select(i => new { provider = i.Provider, label = i.Label, createdUtc = i.CreatedUtc })
};

static int StatusFor(string code) => code switch
{
    "username_taken" => StatusCodes.Status409Conflict,
    "not_found" => StatusCodes.Status404NotFound,
    "identity_in_use" or "provider_already_linked" or "insufficient_funds" or "insufficient_items" => StatusCodes.Status409Conflict,
    "last_login_method" => StatusCodes.Status403Forbidden,
    "unknown_provider" or "unknown_leaderboard" or "unknown_tournament" => StatusCodes.Status404NotFound,
    "tournament_not_active" or "tournament_not_ended" or "limit_reached" or "reward_unclaimed"
        or "expired" or "no_score" or "no_reward" => StatusCodes.Status409Conflict,
    "not_joined" => StatusCodes.Status403Forbidden,
    "invalid_credential" or "invalid_code" or "invalid_refresh_token" => StatusCodes.Status401Unauthorized,
    "too_soon" or "rate_limited" => StatusCodes.Status429TooManyRequests,
    "group_not_found" or "party_not_found" => StatusCodes.Status404NotFound,
    "muted" or "not_a_member" or "not_group_admin" or "not_group_owner" or "insufficient_rank" or "banned_from_group"
        or "not_party_leader" or "not_invited" or "not_friends" => StatusCodes.Status403Forbidden,
    "already_member" or "already_requested" or "already_in_party" or "party_full" or "group_full" or "friend_limit"
        or "too_many_invites" or "group_name_taken" or "group_limit_reached" or "owner_cannot_leave" or "blocked"
        or "party_requires_connection" or "not_online" or "not_in_channel" or "too_many_channels" => StatusCodes.Status409Conflict,
    "product_not_found" or "goal_not_found" or "daily_not_configured" => StatusCodes.Status404NotFound,
    "goal_not_complete" or "already_claimed" or "daily_already_claimed" => StatusCodes.Status409Conflict,
    "product_unavailable" or "purchase_limit_reached" => StatusCodes.Status409Conflict,
    "unknown_match_mode" or "match_not_found" or "match_handler_not_found" => StatusCodes.Status404NotFound,
    "match_create_forbidden" or "kicked_from_match" => StatusCodes.Status403Forbidden,
    "match_full" or "already_queued" or "too_many_tickets" or "too_many_matches" or "matchmaking_disabled"
        or "party_too_large" => StatusCodes.Status409Conflict,
    "match_transports_disabled" or "transport_not_enabled" => StatusCodes.Status503ServiceUnavailable,
    "provider_unavailable" => StatusCodes.Status502BadGateway,
    "sms_not_configured" => StatusCodes.Status503ServiceUnavailable,
    _ => StatusCodes.Status400BadRequest
};

// Compare via SHA-256 so the comparison time does not depend on how many characters match.
public sealed record ProviderAuthRequest(JsonElement Credential, string? Username = null, bool Create = true);
public sealed record AdminNotificationRequest(string? Subject, string? Body, NotificationReward? Reward, int? ExpiresInDays);
public sealed record EconomyAdjustRequest(Dictionary<string, long>? Wallet, Dictionary<string, long>? Items, string? Reason);
public sealed record ConfigWriteRequest(JsonElement Value, bool Public = false);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ProviderBeginRequest(JsonElement Credential);
public sealed record ProviderLinkRequest(JsonElement Credential);

public sealed record DeviceAuthRequest(string DeviceId, string? Username = null, bool Create = true);

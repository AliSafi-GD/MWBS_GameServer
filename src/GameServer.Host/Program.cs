using System.Text;
using System.Threading.RateLimiting;
using GameServer.Abstractions;
using GameServer.Core;
using GameServer.Core.Admin;
using GameServer.Core.Matches;
using GameServer.Core.Realtime;
using GameServer.Host.Endpoints;
using GameServer.Host.Middleware;
using GameServer.Transports.Tcp;
using GameServer.Transports.Udp;
using GameServer.Transports.Ws;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

// Composition root: options, services, auth and rate limits, then the request pipeline, then one Map* call per
// area of the API (the routes live in Endpoints/, the custom middleware in Middleware/).

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

// Login methods: GameServer:Auth:Providers (env GameServer__Auth__Providers, e.g. "device" or "device,googleplay")
// chooses which are on; not set = device plus every configured one. Pin or extend them in code with
//   auth: a => a.Add<AppleIdentityProvider>().Use("device", "apple")
// A game module can also bring its own with init.RegisterIdentityProvider(...).

// Basic abuse protection, counted per client IP. Behind a reverse proxy (nginx, Caddy...) enable forwarded
// headers so the real client IP is used instead of the proxy's.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    // Guest login creates an account per call, so it needs a limit too. Higher than "auth": a whole family or
    // classroom may share one IP, and a first launch is one call per install.
    options.AddPolicy("device", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
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

// Now that modules had their chance to add login providers, check GameServer:Auth:Providers (fails fast if wrong).
app.Services.GetRequiredService<GameServer.Core.Identity.IdentityProviders>().Validate();

// Create/upgrade database tables.
await app.Services.GetRequiredService<GameServer.Core.Postgres.DatabaseMigrator>().MigrateAsync();

// Request pipeline. The order matters.
app.UseGameExceptionHandler(); // GameException -> JSON error with the right status
app.UseMaintenanceMode(); // 503 for the game API while config key "maintenance" is on

app.UseDefaultFiles();
app.UseStaticFiles(); // serves the admin panel from wwwroot/admin
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseGameServerRealtime(); // websocket / tcp / ... endpoints, as chosen at build time
app.UseGameServerMatches(); // match transports (websocket, udp...), as chosen at build time
app.UseAdminAudit(); // records every admin write in the audit log

app.MapGet("/healthz", (ModuleRegistry registry) => Results.Ok(new { status = "ok", rpcs = registry.RpcIds }));

// Endpoints, one file per area in Endpoints/.
app.MapAuth();          // /v1/auth/*
app.MapConfig();        // /v1/config, /v1/version-check
app.MapAccount();       // /v1/account*, /v1/users
app.MapEconomy();       // /v1/wallet, /v1/inventory
app.MapCompetitions();  // /v1/leaderboards, /v1/tournaments
app.MapNotifications(); // /v1/notifications*
app.MapRpc();           // /v1/rpc/{id}
app.MapAdmin();         // /admin/api/* (the panel's API)
app.MapSocial();        // friends, groups/clans, parties
app.MapChat();
app.MapMatches();
app.MapShop();
app.MapProgress();

app.Run();

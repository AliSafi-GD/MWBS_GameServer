using System.Security.Cryptography;
using System.Text.Json;
using GameServer.Abstractions;
using GameServer.Core;

namespace GameServer.Host.Endpoints;

/// <summary>Remote config and version check. No login needed: clients ask before they log in.</summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
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

        return app;
    }

    // "1.2" and "1.2.0" must compare equal, so missing parts become 0.
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v)
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0))
            : null;
    }
}

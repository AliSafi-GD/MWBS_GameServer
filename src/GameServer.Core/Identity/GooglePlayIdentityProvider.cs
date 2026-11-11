using System.Net.Http.Json;
using System.Text.Json;
using GameServer.Abstractions;
using Microsoft.Extensions.Options;

namespace GameServer.Core.Identity;

public sealed class GooglePlayOptions
{
    /// <summary>OAuth client id of the "Web application" credential created for this game in Google Cloud.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret of the same credential. Keep it on the server only.</summary>
    public string ClientSecret { get; set; } = "";
}

/// <summary>
/// Google Play Games login (also used for the Bazaar and Myket builds, which sign in with Google Play Games too).
/// The game client asks Play Games for a server auth code and sends it as <c>{"serverAuthCode": "..."}</c>.
/// The server exchanges it with Google and reads the player id, so the client cannot claim someone else's id.
/// </summary>
public sealed class GooglePlayIdentityProvider : IIdentityProvider
{
    public const string ProviderName = "googleplay";

    private readonly HttpClient _http;
    private readonly GooglePlayOptions _options;

    public GooglePlayIdentityProvider(HttpClient http, IOptions<GooglePlayOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string Name => ProviderName;

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task<VerifiedIdentity> VerifyAsync(JsonElement credential, CancellationToken ct)
    {
        var code = JsonHelpers.GetString(credential, "serverAuthCode");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 2048)
            throw new GameException("invalid_credential", "serverAuthCode is required.");

        // 1) Exchange the one-time auth code for an access token.
        string accessToken;
        try
        {
            using var tokenResponse = await _http.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["grant_type"] = "authorization_code",
                    // Play Games server auth codes are exchanged without a redirect URI on most setups.
                    // If Google answers redirect_uri_mismatch, add the redirect URI from your credential here.
                }),
                ct);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                // 4xx = the code is wrong/expired/used; 5xx = Google is having problems.
                if ((int)tokenResponse.StatusCode >= 500)
                    throw new GameException("provider_unavailable", "Google is not reachable right now.");
                throw new GameException("invalid_credential", "Google rejected the auth code.");
            }

            var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            accessToken = JsonHelpers.GetString(token, "access_token") ?? "";
            if (accessToken.Length == 0)
                throw new GameException("invalid_credential", "Google returned no access token.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GameException("provider_unavailable", "Google is not reachable right now.");
        }

        // 2) Ask Play Games who this is.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/games/v1/players/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500)
                    throw new GameException("provider_unavailable", "Google is not reachable right now.");
                throw new GameException("invalid_credential", "Could not read the Play Games player.");
            }

            var player = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var playerId = JsonHelpers.GetString(player, "playerId");
            if (string.IsNullOrEmpty(playerId))
                throw new GameException("invalid_credential", "Play Games returned no player id.");

            var label = JsonHelpers.GetString(player, "displayName") ?? "";
            if (label.Length > 64) label = label[..64];

            return new VerifiedIdentity(ProviderName, playerId, label);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GameException("provider_unavailable", "Google is not reachable right now.");
        }
    }
}

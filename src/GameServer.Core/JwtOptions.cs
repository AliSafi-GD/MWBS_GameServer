using System.Text;

namespace GameServer.Core;

/// <summary>Bound from "GameServer:Jwt" (env vars: GameServer__Jwt__Secret, ...).</summary>
public sealed class JwtOptions
{
    /// <summary>HMAC signing key. At least 32 bytes. Must be unique per game server and kept secret.</summary>
    public string Secret { get; set; } = "";

    public string Issuer { get; set; } = "gameserver";

    /// <summary>Lifetime of the access token (the JWT). Short on purpose; clients renew it with the refresh token.</summary>
    public int ExpiryMinutes { get; set; } = 60;

    /// <summary>Lifetime of a refresh token. Every use issues a new one, so active players stay logged in.</summary>
    public int RefreshDays { get; set; } = 30;

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Secret) || Encoding.UTF8.GetByteCount(Secret) < 32)
            throw new InvalidOperationException(
                "GameServer:Jwt:Secret must be set to at least 32 bytes " +
                "(set env var GameServer__Jwt__Secret, or use appsettings.Development.json for local development).");
    }
}

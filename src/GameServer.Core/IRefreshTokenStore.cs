namespace GameServer.Core;

/// <summary>Long-lived, single-use tokens that let a client get a new access token without logging in again.</summary>
public interface IRefreshTokenStore
{
    /// <summary>Starts a new session (new family) and returns its first refresh token.</summary>
    Task<string> IssueAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Uses a refresh token: returns the user and the replacement token. Throws GameException("invalid_refresh_token")
    /// if it is unknown, expired, revoked or already used (a used token revokes its whole session).
    /// </summary>
    Task<(string UserId, string RefreshToken)> RotateAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Ends the session this token belongs to. Unknown tokens are ignored.</summary>
    Task RevokeSessionAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Ends every session of the user ("log out everywhere").</summary>
    Task RevokeAllAsync(string userId, CancellationToken ct = default);
}

using System.Security.Cryptography;
using System.Text;
using GameServer.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GameServer.Core.Postgres;

public sealed class PostgresRefreshTokenStore : IRefreshTokenStore
{
    private readonly NpgsqlDataSource _db;
    private readonly int _days;

    public PostgresRefreshTokenStore(NpgsqlDataSource db, IOptions<JwtOptions> options)
    {
        _db = db;
        _days = Math.Max(1, options.Value.RefreshDays);
    }

    public async Task<string> IssueAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        // Housekeeping: drop this user's expired tokens (cheap, indexed by user).
        await using (var clean = new NpgsqlCommand("DELETE FROM refresh_tokens WHERE user_id = @u AND expires_at < now()", conn))
        {
            clean.Parameters.AddWithValue("u", userId);
            await clean.ExecuteNonQueryAsync(ct);
        }

        return await InsertAsync(conn, null, userId, Guid.NewGuid().ToString("N"), ct);
    }

    public async Task<(string UserId, string RefreshToken)> RotateAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = Hash(refreshToken);
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Atomic "use once": only one of two parallel requests can flip used_at.
        string? userId = null, familyId = null;
        await using (var use = new NpgsqlCommand("""
            UPDATE refresh_tokens SET used_at = now()
             WHERE token_hash = @h AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()
            RETURNING user_id, family_id
            """, conn, tx))
        {
            use.Parameters.AddWithValue("h", hash);
            await using var reader = await use.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                userId = reader.GetString(0);
                familyId = reader.GetString(1);
            }
        }

        if (userId is null || familyId is null)
        {
            // A token we know but that is already used/revoked/expired: treat a reused one as theft and end the session.
            await using (var revoke = new NpgsqlCommand("""
                UPDATE refresh_tokens SET revoked_at = now()
                 WHERE revoked_at IS NULL
                   AND family_id = (SELECT family_id FROM refresh_tokens WHERE token_hash = @h)
                """, conn, tx))
            {
                revoke.Parameters.AddWithValue("h", hash);
                await revoke.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            throw new GameException("invalid_refresh_token", "The refresh token is not valid. Log in again.");
        }

        var next = await InsertAsync(conn, tx, userId, familyId, ct);
        await tx.CommitAsync(ct);
        return (userId, next);
    }

    public async Task RevokeSessionAsync(string refreshToken, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            UPDATE refresh_tokens SET revoked_at = now()
             WHERE revoked_at IS NULL
               AND family_id = (SELECT family_id FROM refresh_tokens WHERE token_hash = @h)
            """);
        cmd.Parameters.AddWithValue("h", Hash(refreshToken));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeAllAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE refresh_tokens SET revoked_at = now() WHERE user_id = @u AND revoked_at IS NULL");
        cmd.Parameters.AddWithValue("u", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> InsertAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string userId, string familyId, CancellationToken ct)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO refresh_tokens (token_hash, user_id, family_id, expires_at)
            VALUES (@h, @u, @f, now() + make_interval(days => @d))
            """, conn, tx);
        cmd.Parameters.AddWithValue("h", Hash(token));
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("f", familyId);
        cmd.Parameters.AddWithValue("d", _days);
        await cmd.ExecuteNonQueryAsync(ct);
        return token;
    }

    // The token is 256 random bits, so a plain SHA-256 is enough (no salt or slow hash needed).
    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

using GameServer.Core.Identity;
using Npgsql;

namespace GameServer.Infrastructure.Postgres;

/// <summary>The SQL behind <see cref="IPhoneCodeStore"/> (table phone_otps).</summary>
public sealed class PostgresPhoneCodes : IPhoneCodeStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresPhoneCodes(NpgsqlDataSource db) => _db = db;

    public async Task<bool> TrySaveAsync(string phone, string codeHash, int ttlSeconds, int cooldownSeconds, CancellationToken ct)
    {
        // Upsert, but refuse (0 rows) if a code was sent too recently for this number.
        await using var cmd = _db.CreateCommand("""
            INSERT INTO phone_otps (phone, code_hash, expires_at, attempts, last_sent_at)
            VALUES (@p, @h, now() + make_interval(secs => @ttl), 0, now())
            ON CONFLICT (phone) DO UPDATE
               SET code_hash = EXCLUDED.code_hash,
                   expires_at = EXCLUDED.expires_at,
                   attempts = 0,
                   last_sent_at = now()
             WHERE phone_otps.last_sent_at < now() - make_interval(secs => @cool)
            """);
        cmd.Parameters.AddWithValue("p", phone);
        cmd.Parameters.AddWithValue("h", codeHash);
        cmd.Parameters.AddWithValue("ttl", (double)ttlSeconds);
        cmd.Parameters.AddWithValue("cool", (double)cooldownSeconds);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> CountAttemptAsync(string phone, int maxAttempts, CancellationToken ct)
    {
        await using var attempt = _db.CreateCommand("""
            UPDATE phone_otps SET attempts = attempts + 1
             WHERE phone = @p AND expires_at > now() AND attempts < @max
            RETURNING code_hash
            """);
        attempt.Parameters.AddWithValue("p", phone);
        attempt.Parameters.AddWithValue("max", maxAttempts);
        return await attempt.ExecuteScalarAsync(ct) as string;
    }

    public async Task DeleteAsync(string phone, CancellationToken ct)
    {
        await using var delete = _db.CreateCommand("DELETE FROM phone_otps WHERE phone = @p");
        delete.Parameters.AddWithValue("p", phone);
        await delete.ExecuteNonQueryAsync(ct);
    }
}

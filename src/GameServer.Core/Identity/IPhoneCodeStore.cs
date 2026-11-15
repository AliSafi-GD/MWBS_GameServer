namespace GameServer.Core.Identity;

/// <summary>Where the phone provider keeps its one-time codes (only hashes are stored). Postgres in production, a fake in tests.</summary>
public interface IPhoneCodeStore
{
    /// <summary>
    /// Saves a new code hash for the number, unless one was saved less than <paramref name="cooldownSeconds"/> ago.
    /// Returns false in that case (nothing is changed).
    /// </summary>
    Task<bool> TrySaveAsync(string phone, string codeHash, int ttlSeconds, int cooldownSeconds, CancellationToken ct);

    /// <summary>
    /// Counts one attempt (atomically, so parallel guesses are limited too) and returns the stored hash, or null when
    /// there is no live code or the attempts are used up.
    /// </summary>
    Task<string?> CountAttemptAsync(string phone, int maxAttempts, CancellationToken ct);

    /// <summary>Removes the code, so it can be used only once.</summary>
    Task DeleteAsync(string phone, CancellationToken ct);
}

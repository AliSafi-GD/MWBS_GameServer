namespace GameServer.Core.Progression;

/// <summary>A stat in one period before and after a change.</summary>
public sealed record StatChange(long Old, long New);

/// <summary>
/// Player stats (one counter per stat and period key, see <see cref="PeriodKeys"/>) and which goal rewards were taken.
/// The rules (targets, periods, rewards) are in <see cref="ProgressService"/>; this only stores numbers.
/// </summary>
public interface IProgressStore
{
    /// <summary>
    /// Adds <paramref name="amount"/> to the stat in every given period (or raises it to <paramref name="amount"/> when
    /// <paramref name="max"/>), atomically, and returns the old and new value per period key.
    /// Throws GameException "not_found" when the user does not exist.
    /// </summary>
    Task<IReadOnlyDictionary<string, StatChange>> ChangeStatAsync(
        string userId, string stat, IReadOnlyCollection<string> periodKeys, long amount, bool max, CancellationToken ct);

    /// <summary>The lifetime value of every stat of the player, by name.</summary>
    Task<IReadOnlyDictionary<string, long>> LifetimeStatsAsync(string userId, int limit, CancellationToken ct);

    /// <summary>The values of the given stats in the given periods: (stat, period key) -> value; missing = 0.</summary>
    Task<IReadOnlyDictionary<(string Stat, string Key), long>> StatValuesAsync(
        string userId, IReadOnlyCollection<string> stats, IReadOnlyCollection<string> periodKeys, CancellationToken ct);

    /// <summary>The goals of a kind ("achievement"/"quest") whose reward was taken in the given periods.</summary>
    Task<IReadOnlySet<(string Goal, string Key)>> ClaimsAsync(
        string userId, string kind, IReadOnlyCollection<string> periodKeys, CancellationToken ct);

    /// <summary>Marks a reward as taken. False when it already was (only one request can win).</summary>
    Task<bool> TryClaimAsync(string userId, string kind, string goalId, string periodKey, CancellationToken ct);

    /// <summary>Removes the mark again (when paying the reward failed).</summary>
    Task UnclaimAsync(string userId, string kind, string goalId, string periodKey, CancellationToken ct);
}

namespace GameServer.Abstractions;

/// <summary>
/// Player statistics. Achievements and quests (config keys "achievements" and "quests") are measured against them,
/// so a game only has to report what happened: <c>ctx.Progress.AddStatAsync(userId, "matches_won")</c>.
/// Every change is counted for the lifetime and for the current day, week and month (UTC).
/// </summary>
public interface IProgress
{
    /// <summary>Adds to a stat (delta 1 to 1,000,000,000,000). Returns the new lifetime value.</summary>
    Task<long> AddStatAsync(string userId, string stat, long delta = 1, CancellationToken ct = default);

    /// <summary>Raises a stat to <paramref name="value"/> if that is higher than it is now (best score, highest level).</summary>
    Task<long> SetStatMaxAsync(string userId, string stat, long value, CancellationToken ct = default);

    /// <summary>All lifetime stats of the player.</summary>
    Task<IReadOnlyDictionary<string, long>> GetStatsAsync(string userId, CancellationToken ct = default);
}

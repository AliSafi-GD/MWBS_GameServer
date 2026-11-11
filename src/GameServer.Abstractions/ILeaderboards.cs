namespace GameServer.Abstractions;

/// <param name="Score">The player's score on the board after the update (for "best" this is their best, not the submitted one).</param>
/// <param name="Rank">1 = first place.</param>
public sealed record LeaderboardScore(long Score, int Rank);

/// <summary>
/// Submitting scores. Leaderboards and tournaments are defined in config (keys "leaderboards" and "tournaments"),
/// and clients can only read them, so a module is where a score is validated and then recorded.
/// </summary>
public interface ILeaderboards
{
    /// <summary>
    /// Records a score on a leaderboard. The board's "operator" decides how: best = keep the better one,
    /// set = overwrite, incr = add. Throws GameException "unknown_leaderboard" if it is not defined.
    /// </summary>
    Task<LeaderboardScore> SubmitAsync(string leaderboardId, string userId, long score, CancellationToken ct = default);

    /// <summary>
    /// Records a score in a tournament. The player must have joined and the tournament must be running
    /// ("not_joined", "tournament_not_active", "unknown_tournament").
    /// </summary>
    Task<LeaderboardScore> SubmitTournamentAsync(string tournamentId, string userId, long score, CancellationToken ct = default);
}

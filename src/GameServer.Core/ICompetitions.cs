using System.Text.Json;
using GameServer.Abstractions;

namespace GameServer.Core;

public sealed record BoardEntry(int Rank, string UserId, string Username, string DisplayName, string Avatar, long Score);

/// <param name="Period">'all', '2026-11-08' (daily), '2026-W45' (weekly) or '2026-11' (monthly).</param>
/// <param name="PeriodEndsUtc">When this period ends and the board starts over (null for boards that never reset).</param>
/// <param name="Me">The caller's own row, or null if they have no score here.</param>
public sealed record Board(
    string Id, string Period, DateTime? PeriodEndsUtc, string Sort, IReadOnlyList<BoardEntry> Entries, BoardEntry? Me);

/// <param name="Status">"upcoming", "active" or "ended".</param>
public sealed record TournamentInfo(
    string Id, string Status, JsonElement? Name, DateTime StartUtc, DateTime EndUtc, string Sort,
    IReadOnlyDictionary<string, long> EntryFee, IReadOnlyList<TournamentReward> Rewards, bool Joined, bool Claimed);

public sealed record TournamentClaim(
    int Rank, long Score, IReadOnlyDictionary<string, long> Wallet, IReadOnlyDictionary<string, long> Items);

/// <summary>Leaderboards and tournaments as the host and admin panel see them (modules only get <see cref="ILeaderboards"/>).</summary>
public interface ICompetitions : ILeaderboards
{
    /// <summary>
    /// Top <paramref name="limit"/> entries. With <paramref name="around"/> &gt; 0 and a score for <paramref name="userId"/>,
    /// returns that many entries above and below the player instead. Id "tournament:&lt;id&gt;" reads a tournament board.
    /// </summary>
    Task<Board> GetBoardAsync(string id, string? period, int limit, int around, string? userId, CancellationToken ct = default);

    /// <summary>Removes a player's scores from a board in every period (for cheaters). Returns false if there was nothing.</summary>
    Task<bool> DeleteScoreAsync(string boardId, string userId, CancellationToken ct = default);

    Task<IReadOnlyList<TournamentInfo>> ListTournamentsAsync(string userId, CancellationToken ct = default);

    /// <summary>Joins a running tournament, paying the entry fee (once). Safe to call again.</summary>
    Task<TournamentInfo> JoinTournamentAsync(string tournamentId, string userId, CancellationToken ct = default);

    /// <summary>After the tournament ended: grants the reward for the player's final rank, once.</summary>
    Task<TournamentClaim> ClaimTournamentAsync(string tournamentId, string userId, CancellationToken ct = default);
}

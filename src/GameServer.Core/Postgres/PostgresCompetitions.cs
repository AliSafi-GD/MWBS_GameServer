using System.Text.RegularExpressions;
using GameServer.Abstractions;
using Npgsql;

namespace GameServer.Core.Postgres;

public sealed partial class PostgresCompetitions : ICompetitions
{
    private const long MaxScore = 1_000_000_000_000_000;
    private const string TournamentPrefix = "tournament:";

    private readonly NpgsqlDataSource _db;
    private readonly IConfigStore _config;
    private readonly IEconomy _economy;

    public PostgresCompetitions(NpgsqlDataSource db, IConfigStore config, IEconomy economy)
    {
        _db = db;
        _config = config;
        _economy = economy;
    }

    // A board to read or write: its database key, the period of the moment, and how it sorts.
    private sealed record Target(string Id, string Key, string Period, DateTime? EndsUtc, bool Descending, string Operator);

    // ---- submitting -----------------------------------------------------------------------------------------

    public async Task<LeaderboardScore> SubmitAsync(string leaderboardId, string userId, long score, CancellationToken ct = default)
    {
        if (leaderboardId.StartsWith(TournamentPrefix, StringComparison.Ordinal))
            throw new GameException("unknown_leaderboard", $"Leaderboard '{leaderboardId}' is not defined.");

        return await SubmitAsync(await ResolveAsync(leaderboardId, null, ct), userId, score, ct);
    }

    public async Task<LeaderboardScore> SubmitTournamentAsync(string tournamentId, string userId, long score, CancellationToken ct = default)
    {
        var def = await TournamentAsync(tournamentId, ct);
        RequireActive(def);

        await using (var joined = _db.CreateCommand("SELECT 1 FROM tournament_entries WHERE tournament = @t AND user_id = @u"))
        {
            joined.Parameters.AddWithValue("t", tournamentId);
            joined.Parameters.AddWithValue("u", userId);
            if (await joined.ExecuteScalarAsync(ct) is null)
                throw new GameException("not_joined", "Join the tournament first.");
        }

        return await SubmitAsync(TournamentTarget(def), userId, score, ct);
    }

    private async Task<LeaderboardScore> SubmitAsync(Target t, string userId, long score, CancellationToken ct)
    {
        if (score is > MaxScore or < -MaxScore)
            throw new GameException("invalid_score", "The score is out of range.");

        // best: keep the better value (and the time it was reached, which breaks ties); set: overwrite; incr: add.
        var (value, time) = t.Operator switch
        {
            "set" => ("EXCLUDED.score", "now()"),
            "incr" => ("leaderboard_scores.score + EXCLUDED.score", "now()"),
            _ when t.Descending => ("GREATEST(leaderboard_scores.score, EXCLUDED.score)",
                "CASE WHEN EXCLUDED.score > leaderboard_scores.score THEN now() ELSE leaderboard_scores.updated_at END"),
            _ => ("LEAST(leaderboard_scores.score, EXCLUDED.score)",
                "CASE WHEN EXCLUDED.score < leaderboard_scores.score THEN now() ELSE leaderboard_scores.updated_at END")
        };

        await using var conn = await _db.OpenConnectionAsync(ct);
        long stored;
        DateTime updatedAt;
        try
        {
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO leaderboard_scores (leaderboard, period, user_id, score) VALUES (@l, @p, @u, @s)
                ON CONFLICT (leaderboard, period, user_id) DO UPDATE SET score = {value}, updated_at = {time}
                RETURNING score, updated_at
                """, conn);
            cmd.Parameters.AddWithValue("l", t.Key);
            cmd.Parameters.AddWithValue("p", t.Period);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("s", score);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            stored = reader.GetInt64(0);
            updatedAt = reader.GetDateTime(1);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        {
            throw new GameException("invalid_score", "The score is out of range.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }

        return new LeaderboardScore(stored, await RankAsync(conn, t, stored, updatedAt, userId, ct));
    }

    // ---- reading --------------------------------------------------------------------------------------------

    public async Task<Board> GetBoardAsync(string id, string? period, int limit, int around, string? userId, CancellationToken ct = default)
    {
        var t = await ResolveAsync(id, period, ct);
        limit = Math.Clamp(limit, 1, 100);
        around = Math.Clamp(around, 0, 50);

        await using var conn = await _db.OpenConnectionAsync(ct);

        var me = userId is null ? null : await LoadEntryAsync(conn, t, userId, ct);

        var offset = 0;
        if (around > 0 && me is not null)
        {
            offset = Math.Max(0, me.Rank - 1 - around);
            limit = around * 2 + 1;
        }

        var order = t.Descending ? "DESC" : "ASC";
        var entries = new List<BoardEntry>();
        await using (var cmd = new NpgsqlCommand($"""
            SELECT s.user_id, u.username, u.display_name, u.avatar, s.score
            FROM leaderboard_scores s JOIN users u ON u.id = s.user_id
            WHERE s.leaderboard = @l AND s.period = @p AND NOT u.disabled
            ORDER BY s.score {order}, s.updated_at ASC, s.user_id ASC
            LIMIT @lim OFFSET @off
            """, conn))
        {
            cmd.Parameters.AddWithValue("l", t.Key);
            cmd.Parameters.AddWithValue("p", t.Period);
            cmd.Parameters.AddWithValue("lim", limit);
            cmd.Parameters.AddWithValue("off", offset);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rank = offset;
            while (await reader.ReadAsync(ct))
            {
                rank++;
                entries.Add(new BoardEntry(rank, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
            }
        }

        return new Board(t.Id, t.Period, t.EndsUtc, t.Descending ? "desc" : "asc", entries, me);
    }

    public async Task<bool> DeleteScoreAsync(string boardId, string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM leaderboard_scores WHERE leaderboard = @l AND user_id = @u");
        cmd.Parameters.AddWithValue("l", boardId);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // The player's row with its rank, or null if they have no score on this board/period (or are banned).
    private static async Task<BoardEntry?> LoadEntryAsync(NpgsqlConnection conn, Target t, string userId, CancellationToken ct)
    {
        string username, displayName, avatar;
        long score;
        DateTime updatedAt;

        await using (var cmd = new NpgsqlCommand("""
            SELECT s.score, s.updated_at, u.username, u.display_name, u.avatar
            FROM leaderboard_scores s JOIN users u ON u.id = s.user_id
            WHERE s.leaderboard = @l AND s.period = @p AND s.user_id = @u AND NOT u.disabled
            """, conn))
        {
            cmd.Parameters.AddWithValue("l", t.Key);
            cmd.Parameters.AddWithValue("p", t.Period);
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            score = reader.GetInt64(0);
            updatedAt = reader.GetDateTime(1);
            username = reader.GetString(2);
            displayName = reader.GetString(3);
            avatar = reader.GetString(4);
        }

        var rank = await RankAsync(conn, t, score, updatedAt, userId, ct);
        return new BoardEntry(rank, userId, username, displayName, avatar, score);
    }

    // 1 + number of players ahead. Ties go to whoever reached the score first, then by user id, so ranks are unique.
    private static async Task<int> RankAsync(NpgsqlConnection conn, Target t, long score, DateTime updatedAt, string userId, CancellationToken ct)
    {
        var better = t.Descending ? ">" : "<";
        await using var cmd = new NpgsqlCommand($"""
            SELECT count(*) + 1
            FROM leaderboard_scores s JOIN users u ON u.id = s.user_id
            WHERE s.leaderboard = @l AND s.period = @p AND NOT u.disabled
              AND (s.score {better} @s OR (s.score = @s AND (s.updated_at, s.user_id) < (@t, @u)))
            """, conn);
        cmd.Parameters.AddWithValue("l", t.Key);
        cmd.Parameters.AddWithValue("p", t.Period);
        cmd.Parameters.AddWithValue("s", score);
        cmd.Parameters.AddWithValue("t", updatedAt);
        cmd.Parameters.AddWithValue("u", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---- tournaments ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<TournamentInfo>> ListTournamentsAsync(string userId, CancellationToken ct = default)
    {
        var defs = await TournamentsAsync(ct);

        var mine = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using (var cmd = _db.CreateCommand("SELECT tournament, claimed_at IS NOT NULL FROM tournament_entries WHERE user_id = @u"))
        {
            cmd.Parameters.AddWithValue("u", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                mine[reader.GetString(0)] = reader.GetBoolean(1);
        }

        var now = DateTime.UtcNow;
        return defs.Values
            .Where(d => d.EndUtc > now.AddDays(-30)) // finished long ago: no longer listed
            .OrderBy(d => d.StartUtc)
            .Select(d => ToInfo(d, mine.TryGetValue(d.Id, out var claimed), mine.GetValueOrDefault(d.Id), now))
            .ToList();
    }

    public async Task<TournamentInfo> JoinTournamentAsync(string tournamentId, string userId, CancellationToken ct = default)
    {
        var def = await TournamentAsync(tournamentId, ct);
        RequireActive(def);

        // The fee uses an idempotency key, so retrying (or a crash between the two steps) never charges twice.
        if (def.EntryFee.Count > 0)
        {
            var fee = new EconomyTransaction($"tournament_entry:{def.Id}", $"tournament:{def.Id}:join");
            foreach (var (currency, amount) in def.EntryFee)
                fee.Currency(currency, -amount);
            await _economy.ApplyAsync(userId, fee, ct);
        }

        await using var cmd = _db.CreateCommand(
            "INSERT INTO tournament_entries (tournament, user_id) VALUES (@t, @u) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue("t", def.Id);
        cmd.Parameters.AddWithValue("u", userId);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("not_found", "User not found.");
        }

        return ToInfo(def, true, false, DateTime.UtcNow);
    }

    public async Task<TournamentClaim> ClaimTournamentAsync(string tournamentId, string userId, CancellationToken ct = default)
    {
        var def = await TournamentAsync(tournamentId, ct);
        if (DateTime.UtcNow < def.EndUtc)
            throw new GameException("tournament_not_ended", "The tournament has not ended yet.");

        bool claimed;
        await using (var entry = _db.CreateCommand(
            "SELECT claimed_at IS NOT NULL FROM tournament_entries WHERE tournament = @t AND user_id = @u"))
        {
            entry.Parameters.AddWithValue("t", def.Id);
            entry.Parameters.AddWithValue("u", userId);
            var result = await entry.ExecuteScalarAsync(ct);
            if (result is null)
                throw new GameException("not_joined", "You did not join this tournament.");
            claimed = (bool)result;
        }
        if (claimed)
            throw new GameException("already_claimed", "The reward was already claimed.");

        await using var conn = await _db.OpenConnectionAsync(ct);
        var me = await LoadEntryAsync(conn, TournamentTarget(def), userId, ct)
                 ?? throw new GameException("no_score", "You have no score in this tournament.");

        var tier = def.Rewards.FirstOrDefault(r => me.Rank >= r.FromRank && me.Rank <= r.ToRank);
        IReadOnlyDictionary<string, long> wallet = tier?.Wallet ?? new Dictionary<string, long>();
        IReadOnlyDictionary<string, long> items = tier?.Items ?? new Dictionary<string, long>();

        if (tier is not null)
        {
            // Idempotency key: a retry after a crash does not pay twice.
            var tx = new EconomyTransaction($"tournament_reward:{def.Id}", $"tournament:{def.Id}:reward");
            foreach (var (name, amount) in wallet) tx.Currency(name, amount);
            foreach (var (name, amount) in items) tx.Item(name, amount);
            await _economy.ApplyAsync(userId, tx, ct);
        }

        await using var mark = new NpgsqlCommand(
            "UPDATE tournament_entries SET claimed_at = now() WHERE tournament = @t AND user_id = @u AND claimed_at IS NULL", conn);
        mark.Parameters.AddWithValue("t", def.Id);
        mark.Parameters.AddWithValue("u", userId);
        await mark.ExecuteNonQueryAsync(ct);

        return new TournamentClaim(me.Rank, me.Score, wallet, items);
    }

    private static TournamentInfo ToInfo(TournamentDef d, bool joined, bool claimed, DateTime now) => new(
        d.Id,
        now < d.StartUtc ? "upcoming" : now >= d.EndUtc ? "ended" : "active",
        d.Name, d.StartUtc, d.EndUtc, d.Descending ? "desc" : "asc", d.EntryFee, d.Rewards, joined, claimed);

    private static void RequireActive(TournamentDef def)
    {
        var now = DateTime.UtcNow;
        if (now < def.StartUtc || now >= def.EndUtc)
            throw new GameException("tournament_not_active", "The tournament is not running right now.");
    }

    private static Target TournamentTarget(TournamentDef d) =>
        new(TournamentPrefix + d.Id, TournamentPrefix + d.Id, "all", d.EndUtc, d.Descending, d.Operator);

    // ---- definitions (from config) --------------------------------------------------------------------------

    private async Task<IReadOnlyDictionary<string, TournamentDef>> TournamentsAsync(CancellationToken ct) =>
        GameDefinitions.ParseTournaments((await _config.GetAsync(ConfigRules.Tournaments, ct))?.Value);

    private async Task<TournamentDef> TournamentAsync(string id, CancellationToken ct) =>
        (await TournamentsAsync(ct)).TryGetValue(id, out var def)
            ? def
            : throw new GameException("unknown_tournament", $"Tournament '{id}' is not defined.");

    private async Task<Target> ResolveAsync(string id, string? periodOverride, CancellationToken ct)
    {
        if (id.StartsWith(TournamentPrefix, StringComparison.Ordinal))
            return TournamentTarget(await TournamentAsync(id[TournamentPrefix.Length..], ct));

        var boards = GameDefinitions.ParseLeaderboards((await _config.GetAsync(ConfigRules.Leaderboards, ct))?.Value);
        if (!boards.TryGetValue(id, out var def))
            throw new GameException("unknown_leaderboard", $"Leaderboard '{id}' is not defined.");

        var now = DateTime.UtcNow;
        var period = PeriodKey(def.Reset, now);
        DateTime? ends = PeriodEnd(def.Reset, now);

        if (periodOverride is not null && periodOverride != "current")
        {
            if (!PeriodRegex().IsMatch(periodOverride))
                throw new GameException("invalid_period", "period must be like all, 2026-11-08, 2026-W45 or 2026-11.");
            if (periodOverride != period) ends = null; // an old period: it has already ended
            period = periodOverride;
        }

        return new Target(id, id, period, ends, def.Descending, def.Operator);
    }

    internal static string PeriodKey(string reset, DateTime utc) => reset switch
    {
        "daily" => utc.ToString("yyyy-MM-dd"),
        "weekly" => $"{System.Globalization.ISOWeek.GetYear(utc)}-W{System.Globalization.ISOWeek.GetWeekOfYear(utc):00}",
        "monthly" => utc.ToString("yyyy-MM"),
        _ => "all"
    };

    internal static DateTime? PeriodEnd(string reset, DateTime utc) => reset switch
    {
        "daily" => utc.Date.AddDays(1),
        "weekly" => utc.Date.AddDays(7 - ((int)utc.DayOfWeek + 6) % 7), // next Monday 00:00 UTC
        "monthly" => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1),
        _ => null
    };

    [GeneratedRegex(@"^(all|\d{4}-\d{2}-\d{2}|\d{4}-W\d{2}|\d{4}-\d{2})\z")]
    private static partial Regex PeriodRegex();
}

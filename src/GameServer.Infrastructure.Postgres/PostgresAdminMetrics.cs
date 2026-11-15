using GameServer.Abstractions;
using GameServer.Core.Admin;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Infrastructure.Postgres;

/// <summary>Dashboard numbers: per-minute samples in metrics_samples, totals from users and ledger.</summary>
public sealed class PostgresAdminMetrics : IAdminMetrics
{
    private readonly NpgsqlDataSource _db;

    public PostgresAdminMetrics(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task SaveSampleAsync(int online, int matches, int queued, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO metrics_samples (at, online, matches, queued) VALUES (date_trunc('minute', now()), @o, @m, @q)
            ON CONFLICT (at) DO UPDATE SET online = GREATEST(metrics_samples.online, EXCLUDED.online),
                                           matches = EXCLUDED.matches, queued = EXCLUDED.queued
            """);
        cmd.Parameters.AddWithValue("o", online);
        cmd.Parameters.AddWithValue("m", matches);
        cmd.Parameters.AddWithValue("q", queued);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("""
            DELETE FROM metrics_samples WHERE at < now() - interval '7 days';
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MetricPoint>> SeriesAsync(int minutes, CancellationToken ct)
    {
        minutes = Math.Clamp(minutes, 5, 7 * 24 * 60);
        var bucket = Math.Max(1, minutes / 120);
        await using var cmd = _db.CreateCommand("""
            SELECT to_timestamp(floor(extract(epoch FROM at) / (@b * 60)) * (@b * 60)) AS t,
                   max(online)::int, max(matches)::int, max(queued)::int
            FROM metrics_samples
            WHERE at > now() - make_interval(mins => @m)
            GROUP BY 1 ORDER BY 1
            """);
        cmd.Parameters.AddWithValue("b", bucket);
        cmd.Parameters.AddWithValue("m", minutes);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<MetricPoint>();
        while (await r.ReadAsync(ct))
            list.Add(new MetricPoint(r.GetDateTime(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    public async Task<DashboardTotals> TotalsAsync(CancellationToken ct)
    {
        int users, banned, s24, l24, s7;
        await using (var cmd = _db.CreateCommand("""
            SELECT count(*)::int,
                   count(*) FILTER (WHERE disabled)::int,
                   count(*) FILTER (WHERE created_at > now() - interval '24 hours')::int,
                   count(*) FILTER (WHERE last_login_at > now() - interval '24 hours')::int,
                   count(*) FILTER (WHERE created_at > now() - interval '7 days')::int
            FROM users
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (users, banned, s24, l24, s7) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }

        var economy = new List<EconomyFlow>();
        await using (var cmd = _db.CreateCommand("""
            SELECT name,
                   COALESCE(sum(delta) FILTER (WHERE delta > 0), 0)::bigint,
                   COALESCE(-sum(delta) FILTER (WHERE delta < 0), 0)::bigint
            FROM ledger
            WHERE kind = 'wallet' AND created_at > now() - interval '24 hours'
            GROUP BY name ORDER BY name
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                economy.Add(new EconomyFlow(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));

        var perDay = new List<DayCount>();
        await using (var cmd = _db.CreateCommand("""
            SELECT to_char(d::date, 'YYYY-MM-DD'), COALESCE(c.n, 0)::int
            FROM generate_series(current_date - 6, current_date, interval '1 day') d
            LEFT JOIN (SELECT created_at::date AS day, count(*) AS n FROM users
                       WHERE created_at >= current_date - 6 GROUP BY 1) c ON c.day = d::date
            ORDER BY d
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                perDay.Add(new DayCount(r.GetString(0), r.GetInt32(1)));

        return new DashboardTotals(users, banned, s24, l24, s7, economy, perDay);
    }
}

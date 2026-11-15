using GameServer.Abstractions;
using GameServer.Core.Progression;
using Npgsql;

namespace GameServer.Core.Postgres;

/// <summary>Login streaks in table daily_rewards (user -> streak, last_date). A claim holds the row lock in a transaction.</summary>
public sealed class PostgresDailyRewards : IDailyRewardStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresDailyRewards(NpgsqlDataSource db) => _db = db;

    public async Task<DailyState> GetAsync(string userId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT streak, last_date FROM daily_rewards WHERE user_id = @u");
        cmd.Parameters.AddWithValue("u", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : new DailyState(0, null);
    }

    public async Task<IDailyClaim> BeginClaimAsync(string userId, CancellationToken ct)
    {
        var conn = await _db.OpenConnectionAsync(ct);
        NpgsqlTransaction? tx = null;
        try
        {
            tx = await conn.BeginTransactionAsync(ct);

            try
            {
                await using var ensure = new NpgsqlCommand("INSERT INTO daily_rewards (user_id) VALUES (@u) ON CONFLICT DO NOTHING", conn, tx);
                ensure.Parameters.AddWithValue("u", userId);
                await ensure.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new GameException("not_found", "User not found.");
            }

            // The row stays locked until the commit, so two phones cannot both claim the same day.
            DailyState state;
            await using (var sel = new NpgsqlCommand("SELECT streak, last_date FROM daily_rewards WHERE user_id = @u FOR UPDATE", conn, tx))
            {
                sel.Parameters.AddWithValue("u", userId);
                await using var reader = await sel.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                state = Read(reader);
            }

            return new Claim(conn, tx, userId, state);
        }
        catch
        {
            if (tx is not null) await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
    }

    private static DailyState Read(NpgsqlDataReader reader) =>
        new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1));

    private sealed class Claim : IDailyClaim
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction _tx;
        private readonly string _userId;

        public Claim(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, DailyState state)
        {
            _conn = conn;
            _tx = tx;
            _userId = userId;
            State = state;
        }

        public DailyState State { get; }

        public async Task SaveAsync(int streak, DateOnly date, CancellationToken ct)
        {
            await using var upd = new NpgsqlCommand("UPDATE daily_rewards SET streak = @s, last_date = @d WHERE user_id = @u", _conn, _tx);
            upd.Parameters.AddWithValue("s", streak);
            upd.Parameters.AddWithValue("d", date);
            upd.Parameters.AddWithValue("u", _userId);
            await upd.ExecuteNonQueryAsync(ct);
        }

        public Task CommitAsync(CancellationToken ct) => _tx.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await _tx.DisposeAsync(); // rolls back if not committed
            await _conn.DisposeAsync();
        }
    }
}

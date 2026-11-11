using Microsoft.Extensions.Logging;
using Npgsql;

namespace GameServer.Core.Postgres;

/// <summary>
/// Applies core schema migrations at startup. Each migration runs once (tracked in schema_migrations).
/// To change the schema later, ADD a new entry with the next version; never edit an applied one.
/// </summary>
public sealed class DatabaseMigrator
{
    private const long LockKey = 727_274_001; // advisory lock so two instances don't migrate at once

    private static readonly (int Version, string Sql)[] Migrations =
    {
        (1, """
            CREATE TABLE users (
                id          text PRIMARY KEY,
                created_at  timestamptz NOT NULL DEFAULT now()
            );

            -- One user can have several login methods later (email, Google...), so devices get their own table.
            CREATE TABLE user_devices (
                device_id   text PRIMARY KEY,
                user_id     text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                created_at  timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX user_devices_user_id_idx ON user_devices (user_id);

            CREATE TABLE storage (
                user_id     text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                key         text NOT NULL,
                value       jsonb NOT NULL,
                updated_at  timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (user_id, key)
            );
            """),

        (2, """
            ALTER TABLE users
                ADD COLUMN username       text,
                ADD COLUMN display_name   text        NOT NULL DEFAULT '',
                ADD COLUMN avatar         text        NOT NULL DEFAULT '',
                ADD COLUMN lang           text        NOT NULL DEFAULT 'en',
                ADD COLUMN timezone       text        NOT NULL DEFAULT '',
                ADD COLUMN updated_at     timestamptz NOT NULL DEFAULT now(),
                ADD COLUMN last_login_at  timestamptz,
                ADD COLUMN disabled       boolean     NOT NULL DEFAULT false;

            -- Accounts created before this migration get a placeholder username.
            UPDATE users SET username = 'player_' || substr(id, 1, 8) WHERE username IS NULL;

            ALTER TABLE users
                ALTER COLUMN username SET NOT NULL,
                ADD CONSTRAINT users_username_key UNIQUE (username);
            """),

        (3, """
            -- External logins (Google Play Games, phone number, later Apple, CrazyGames...).
            -- Devices stay in user_devices because one user may have several of them.
            -- An account can have at most one login per provider; a login belongs to exactly one account.
            CREATE TABLE user_identities (
                provider    text        NOT NULL,
                subject     text        NOT NULL,
                user_id     text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                label       text        NOT NULL DEFAULT '',
                created_at  timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (provider, subject),
                UNIQUE (user_id, provider)
            );

            -- One-time codes for phone login. Only a keyed hash of the code is stored.
            CREATE TABLE phone_otps (
                phone         text        PRIMARY KEY,
                code_hash     text        NOT NULL,
                expires_at    timestamptz NOT NULL,
                attempts      integer     NOT NULL DEFAULT 0,
                last_sent_at  timestamptz NOT NULL DEFAULT now()
            );
            """),

        (4, """
            -- Refresh tokens: only a SHA-256 hash is stored. Each use marks the token as used and issues a new one
            -- in the same family; presenting a used token again revokes the whole family (theft detection).
            CREATE TABLE refresh_tokens (
                token_hash  text        PRIMARY KEY,
                user_id     text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                family_id   text        NOT NULL,
                created_at  timestamptz NOT NULL DEFAULT now(),
                expires_at  timestamptz NOT NULL,
                used_at     timestamptz,
                revoked_at  timestamptz
            );
            CREATE INDEX refresh_tokens_user_idx ON refresh_tokens (user_id);
            CREATE INDEX refresh_tokens_family_idx ON refresh_tokens (family_id);
            """),

        (5, """
            -- Remote config: free-form JSON per key. Public keys are served to every client without login.
            CREATE TABLE config (
                key         text        PRIMARY KEY,
                value       jsonb       NOT NULL,
                is_public   boolean     NOT NULL DEFAULT false,
                updated_at  timestamptz NOT NULL DEFAULT now()
            );
            """),

        (6, """
            -- Wallet (currencies) and inventory (stackable items). CHECK (>= 0) is what stops overspending.
            CREATE TABLE wallet_balances (
                user_id     text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                currency    text        NOT NULL,
                balance     bigint      NOT NULL CHECK (balance >= 0),
                updated_at  timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (user_id, currency)
            );

            CREATE TABLE inventory_items (
                user_id     text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                item_id     text        NOT NULL,
                quantity    bigint      NOT NULL CHECK (quantity >= 0),
                updated_at  timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (user_id, item_id)
            );

            -- Permanent history of every change (one row per currency/item per transaction).
            -- ref = idempotency key; the unique index stops the same transaction being applied twice.
            CREATE TABLE ledger (
                id             bigserial   PRIMARY KEY,
                user_id        text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                kind           text        NOT NULL,
                name           text        NOT NULL,
                delta          bigint      NOT NULL,
                balance_after  bigint      NOT NULL,
                reason         text        NOT NULL,
                ref            text,
                created_at     timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX ledger_user_idx ON ledger (user_id, id DESC);
            CREATE UNIQUE INDEX ledger_ref_idx ON ledger (user_id, ref, kind, name) WHERE ref IS NOT NULL;
            """),

        (7, """
            -- One row per player per board per period. "period" is 'all', '2026-11-08', '2026-W45' or '2026-11', so a new
            -- day/week/month simply starts new rows (no reset job) and old periods stay as history.
            -- Tournaments use the board key 'tournament:<id>' with period 'all'.
            CREATE TABLE leaderboard_scores (
                leaderboard  text        NOT NULL,
                period       text        NOT NULL,
                user_id      text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                score        bigint      NOT NULL,
                updated_at   timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (leaderboard, period, user_id)
            );
            CREATE INDEX leaderboard_scores_rank_idx ON leaderboard_scores (leaderboard, period, score DESC);
            CREATE INDEX leaderboard_scores_user_idx ON leaderboard_scores (user_id);

            CREATE TABLE tournament_entries (
                tournament  text        NOT NULL,
                user_id     text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                joined_at   timestamptz NOT NULL DEFAULT now(),
                claimed_at  timestamptz,
                PRIMARY KEY (tournament, user_id)
            );
            CREATE INDEX tournament_entries_user_idx ON tournament_entries (user_id);

            CREATE TABLE notifications (
                id          bigserial   PRIMARY KEY,
                user_id     text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                subject     text        NOT NULL,
                body        text,
                reward      jsonb,
                created_at  timestamptz NOT NULL DEFAULT now(),
                expires_at  timestamptz,
                read_at     timestamptz,
                claimed_at  timestamptz
            );
            CREATE INDEX notifications_user_idx ON notifications (user_id, id DESC);
            """),

        (8, """
            -- Friends: two rows per relationship (one per side). state: 0 friend, 1 invite sent, 2 invite received, 3 blocked.
            CREATE TABLE friendships (
                user_id    text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                friend_id  text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                state      smallint    NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (user_id, friend_id),
                CHECK (user_id <> friend_id)
            );
            CREATE INDEX friendships_friend_idx ON friendships (friend_id);

            -- Clans and groups are the same thing; "type" picks the rules from the "social" config.
            CREATE TABLE groups (
                id           text        PRIMARY KEY,
                name         text        NOT NULL,
                description  text        NOT NULL DEFAULT '',
                avatar       text        NOT NULL DEFAULT '',
                type         text        NOT NULL,
                is_open      boolean     NOT NULL DEFAULT false,
                max_members  integer     NOT NULL,
                creator_id   text        REFERENCES users(id) ON DELETE SET NULL,
                created_at   timestamptz NOT NULL DEFAULT now(),
                updated_at   timestamptz NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX groups_name_idx ON groups (lower(name));
            CREATE INDEX groups_type_idx ON groups (type);

            -- role: 0 owner, 1 admin, 2 member, 3 join requested, 4 invited, 5 banned.
            CREATE TABLE group_members (
                group_id   text        NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                user_id    text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                role       smallint    NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (group_id, user_id)
            );
            CREATE INDEX group_members_user_idx ON group_members (user_id);

            CREATE TABLE chat_messages (
                id         bigserial   PRIMARY KEY,
                channel    text        NOT NULL,
                sender_id  text        REFERENCES users(id) ON DELETE CASCADE,
                text       text        NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX chat_messages_channel_idx ON chat_messages (channel, id DESC);
            """),

        (9, """
            -- Players an admin has muted in chat. until_utc NULL = until an admin lifts it.
            CREATE TABLE chat_mutes (
                user_id    text        PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
                until_utc  timestamptz,
                reason     text,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            """),

        (10, """
            -- How much of each limited shop product a player bought in a period (day, week, month or "all").
            CREATE TABLE shop_purchases (
                user_id    text    NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                product_id text    NOT NULL,
                period_key text    NOT NULL,
                count      integer NOT NULL DEFAULT 0,
                PRIMARY KEY (user_id, product_id, period_key)
            );
            """),

        (11, """
            -- Player stats, counted for the lifetime ("all") and for each day, ISO week and month.
            CREATE TABLE player_stats (
                user_id    text   NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                name       text   NOT NULL,
                period_key text   NOT NULL,
                value      bigint NOT NULL DEFAULT 0,
                PRIMARY KEY (user_id, name, period_key)
            );

            -- Rewards of achievements and quests that were already taken (a quest again in each new period).
            CREATE TABLE goal_claims (
                user_id    text        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                kind       text        NOT NULL,
                goal_id    text        NOT NULL,
                period_key text        NOT NULL,
                claimed_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (user_id, kind, goal_id, period_key)
            );

            -- Login streak of the daily reward.
            CREATE TABLE daily_rewards (
                user_id   text    PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
                streak    integer NOT NULL DEFAULT 0,
                last_date date
            );
            """),

        (12, """
            -- Admin panel: extra admin accounts with roles (the master key from settings is "root" and is not stored here).
            CREATE TABLE admin_accounts (
                id           text        PRIMARY KEY,
                name         text        NOT NULL UNIQUE,
                role         text        NOT NULL,
                key_hash     text        NOT NULL UNIQUE,
                created_at   timestamptz NOT NULL DEFAULT now(),
                created_by   text,
                disabled     boolean     NOT NULL DEFAULT false,
                last_used_at timestamptz
            );

            -- Every change made through the admin API (who, what, result).
            CREATE TABLE admin_audit (
                id     bigserial   PRIMARY KEY,
                at     timestamptz NOT NULL DEFAULT now(),
                actor  text        NOT NULL,
                role   text        NOT NULL,
                method text        NOT NULL,
                path   text        NOT NULL,
                body   text,
                status integer     NOT NULL,
                ip     text
            );
            CREATE INDEX admin_audit_at_idx ON admin_audit (at DESC);
            CREATE INDEX admin_audit_actor_idx ON admin_audit (actor, at DESC);

            -- Previous values of config keys, so a bad change can be rolled back.
            CREATE TABLE config_history (
                id         bigserial   PRIMARY KEY,
                key        text        NOT NULL,
                value      jsonb,
                is_public  boolean     NOT NULL DEFAULT false,
                changed_by text,
                at         timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX config_history_key_idx ON config_history (key, id DESC);

            -- Messages the operator scheduled for later (live-ops).
            CREATE TABLE scheduled_broadcasts (
                id         bigserial   PRIMARY KEY,
                run_at     timestamptz NOT NULL,
                subject    text        NOT NULL,
                body       text,
                reward     jsonb,
                audience   text        NOT NULL DEFAULT 'all',
                expires_in_days integer,
                status     text        NOT NULL DEFAULT 'pending',
                created_by text,
                created_at timestamptz NOT NULL DEFAULT now(),
                sent_at    timestamptz,
                sent_count integer,
                error      text
            );
            CREATE INDEX scheduled_broadcasts_due_idx ON scheduled_broadcasts (status, run_at);

            -- One sample per minute for the dashboard charts.
            CREATE TABLE metrics_samples (
                at      timestamptz PRIMARY KEY,
                online  integer NOT NULL,
                matches integer NOT NULL,
                queued  integer NOT NULL
            );
            """),
    };

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<DatabaseMigrator> _log;

    public DatabaseMigrator(NpgsqlDataSource db, ILogger<DatabaseMigrator> log)
    {
        _db = db;
        _log = log;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        await ExecAsync(conn, null,
            "CREATE TABLE IF NOT EXISTS schema_migrations (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);
        await ExecAsync(conn, null, $"SELECT pg_advisory_lock({LockKey})", ct);

        try
        {
            foreach (var (version, sql) in Migrations)
            {
                await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE version = @v", conn))
                {
                    check.Parameters.AddWithValue("v", version);
                    if (await check.ExecuteScalarAsync(ct) is not null)
                        continue;
                }

                _log.LogInformation("Applying database migration {Version}", version);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await ExecAsync(conn, tx, sql, ct);
                await using (var mark = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@v)", conn, tx))
                {
                    mark.Parameters.AddWithValue("v", version);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await ExecAsync(conn, null, $"SELECT pg_advisory_unlock({LockKey})", ct);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

using GameServer.Abstractions;
using GameServer.Core.Social;
using Npgsql;

namespace GameServer.Core.Postgres;

public sealed class PostgresGroups : IGroupStore
{
    // Column order is relied on by ReadGroup. member_count only counts owner, admins and members (role <= 2).
    private const string GroupCols =
        "g.id, g.name, g.description, g.avatar, g.type, g.is_open, g.max_members, g.creator_id, g.created_at, " +
        "(SELECT count(*) FROM group_members m WHERE m.group_id = g.id AND m.role <= 2)";

    private readonly NpgsqlDataSource _db;

    public PostgresGroups(NpgsqlDataSource db) => _db = db;

    private static GroupInfo ReadGroup(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5),
        r.GetInt32(6), (int)r.GetInt64(9), r.IsDBNull(7) ? null : r.GetString(7), r.GetDateTime(8));

    public async Task<GroupInfo> CreateAsync(NewGroup group, CancellationToken ct = default)
    {
        await using (var conn = await _db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            try
            {
                await using (var insert = new NpgsqlCommand(
                    "INSERT INTO groups (id, name, description, avatar, type, is_open, max_members, creator_id) " +
                    "VALUES (@id, @name, @desc, @avatar, @type, @open, @max, @creator)", conn, tx))
                {
                    insert.Parameters.AddWithValue("id", group.Id);
                    insert.Parameters.AddWithValue("name", group.Name);
                    insert.Parameters.AddWithValue("desc", group.Description);
                    insert.Parameters.AddWithValue("avatar", group.Avatar);
                    insert.Parameters.AddWithValue("type", group.Type);
                    insert.Parameters.AddWithValue("open", group.Open);
                    insert.Parameters.AddWithValue("max", group.MaxMembers);
                    insert.Parameters.AddWithValue("creator", group.CreatorId);
                    await insert.ExecuteNonQueryAsync(ct);
                }

                await using (var owner = new NpgsqlCommand(
                    "INSERT INTO group_members (group_id, user_id, role) VALUES (@g, @u, 0)", conn, tx))
                {
                    owner.Parameters.AddWithValue("g", group.Id);
                    owner.Parameters.AddWithValue("u", group.CreatorId);
                    await owner.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new GameException("group_name_taken", "A group with this name already exists.");
            }
        }

        return (await GetAsync(group.Id, ct))!;
    }

    public async Task<GroupInfo?> GetAsync(string groupId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT {GroupCols} FROM groups g WHERE g.id = @id");
        cmd.Parameters.AddWithValue("id", groupId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadGroup(reader) : null;
    }

    public async Task<GroupInfo?> UpdateAsync(string groupId, GroupPatch patch, CancellationToken ct = default)
    {
        var sets = new List<string> { "updated_at = now()" };
        await using var cmd = _db.CreateCommand("");
        cmd.Parameters.AddWithValue("id", groupId);

        if (patch.Name is not null) { sets.Add("name = @name"); cmd.Parameters.AddWithValue("name", patch.Name); }
        if (patch.Description is not null) { sets.Add("description = @desc"); cmd.Parameters.AddWithValue("desc", patch.Description); }
        if (patch.Avatar is not null) { sets.Add("avatar = @avatar"); cmd.Parameters.AddWithValue("avatar", patch.Avatar); }
        if (patch.Open is not null) { sets.Add("is_open = @open"); cmd.Parameters.AddWithValue("open", patch.Open.Value); }
        if (patch.MaxMembers is not null) { sets.Add("max_members = @max"); cmd.Parameters.AddWithValue("max", patch.MaxMembers.Value); }

        cmd.CommandText = $"UPDATE groups SET {string.Join(", ", sets)} WHERE id = @id";

        try
        {
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                return null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GameException("group_name_taken", "A group with this name already exists.");
        }

        return await GetAsync(groupId, ct);
    }

    public async Task<bool> DeleteAsync(string groupId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM groups WHERE id = @id");
        cmd.Parameters.AddWithValue("id", groupId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<GroupInfo>> SearchAsync(string? query, string? type, int limit, CancellationToken ct = default)
    {
        var sql = $"SELECT {GroupCols} FROM groups g WHERE 1 = 1" +
                  (string.IsNullOrEmpty(type) ? "" : " AND g.type = @type") +
                  (string.IsNullOrWhiteSpace(query) ? "" : " AND lower(g.name) LIKE @pattern ESCAPE '\\'") +
                  " ORDER BY lower(g.name) LIMIT @lim";

        await using var cmd = _db.CreateCommand(sql);
        if (!string.IsNullOrEmpty(type))
            cmd.Parameters.AddWithValue("type", type);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var escaped = query.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("pattern", escaped + "%");
        }
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 100));

        var result = new List<GroupInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadGroup(reader));
        return result;
    }

    public async Task<IReadOnlyList<(GroupInfo Group, GroupRole Role)>> ListForUserAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT {GroupCols}, mine.role FROM group_members mine JOIN groups g ON g.id = mine.group_id " +
            "WHERE mine.user_id = @u AND mine.role <= 4 ORDER BY mine.role, lower(g.name)");
        cmd.Parameters.AddWithValue("u", userId);

        var result = new List<(GroupInfo, GroupRole)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((ReadGroup(reader), (GroupRole)reader.GetInt16(10)));
        return result;
    }

    public async Task<GroupRole?> GetRoleAsync(string groupId, string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT role FROM group_members WHERE group_id = @g AND user_id = @u");
        cmd.Parameters.AddWithValue("g", groupId);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteScalarAsync(ct) is short role ? (GroupRole)role : null;
    }

    public async Task SetRoleAsync(string groupId, string userId, GroupRole role, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(UpsertRoleSql);
        cmd.Parameters.AddWithValue("g", groupId);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("r", (short)role);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new GameException("group_not_found", "Group or player not found.");
        }
    }

    public async Task AddMemberAsync(string groupId, string userId, GroupRole role, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the group row so two players taking the last place cannot both get in.
        int max;
        await using (var lockGroup = new NpgsqlCommand("SELECT max_members FROM groups WHERE id = @g FOR UPDATE", conn, tx))
        {
            lockGroup.Parameters.AddWithValue("g", groupId);
            if (await lockGroup.ExecuteScalarAsync(ct) is not int value)
                throw new GameException("group_not_found", "Group not found.");
            max = value;
        }

        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM group_members WHERE group_id = @g AND role <= 2 AND user_id <> @u", conn, tx))
        {
            count.Parameters.AddWithValue("g", groupId);
            count.Parameters.AddWithValue("u", userId);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) >= max)
                throw new GameException("group_full", "This group is full.");
        }

        await using (var upsert = new NpgsqlCommand(UpsertRoleSql, conn, tx))
        {
            upsert.Parameters.AddWithValue("g", groupId);
            upsert.Parameters.AddWithValue("u", userId);
            upsert.Parameters.AddWithValue("r", (short)role);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<bool> RemoveMemberAsync(string groupId, string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM group_members WHERE group_id = @g AND user_id = @u");
        cmd.Parameters.AddWithValue("g", groupId);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task TransferOwnershipAsync(string groupId, string fromUserId, string toUserId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var demote = new NpgsqlCommand(
            "UPDATE group_members SET role = 1, updated_at = now() WHERE group_id = @g AND user_id = @from AND role = 0", conn, tx))
        {
            demote.Parameters.AddWithValue("g", groupId);
            demote.Parameters.AddWithValue("from", fromUserId);
            if (await demote.ExecuteNonQueryAsync(ct) == 0)
                throw new GameException("not_group_owner", "Only the owner can hand over the group.");
        }

        await using (var promote = new NpgsqlCommand(
            "UPDATE group_members SET role = 0, updated_at = now() WHERE group_id = @g AND user_id = @to AND role IN (1, 2)", conn, tx))
        {
            promote.Parameters.AddWithValue("g", groupId);
            promote.Parameters.AddWithValue("to", toUserId);
            if (await promote.ExecuteNonQueryAsync(ct) == 0)
                throw new GameException("not_found", "The new owner must be a member of the group.");
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<GroupMemberInfo>> ListMembersAsync(
        string groupId, GroupRole fromRole, GroupRole toRole, int limit, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT u.id, u.username, u.display_name, u.avatar, u.created_at, m.role, m.updated_at " +
            "FROM group_members m JOIN users u ON u.id = m.user_id " +
            "WHERE m.group_id = @g AND m.role >= @from AND m.role <= @to AND NOT u.disabled " +
            "ORDER BY m.role, lower(u.username) LIMIT @lim");
        cmd.Parameters.AddWithValue("g", groupId);
        cmd.Parameters.AddWithValue("from", (short)fromRole);
        cmd.Parameters.AddWithValue("to", (short)toRole);
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 500));

        var result = new List<GroupMemberInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var profile = new PublicProfile(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
            result.Add(new GroupMemberInfo(profile, GroupService.RoleName((GroupRole)reader.GetInt16(5)), reader.GetDateTime(6)));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> MemberIdsAsync(string groupId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT user_id FROM group_members WHERE group_id = @g AND role <= 2");
        cmd.Parameters.AddWithValue("g", groupId);

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<int> CountMembershipsAsync(string userId, string type, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT count(*) FROM group_members m JOIN groups g ON g.id = m.group_id " +
            "WHERE m.user_id = @u AND m.role <= 2 AND g.type = @t");
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("t", type);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<string>> OwnedGroupIdsAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT group_id FROM group_members WHERE user_id = @u AND role = 0");
        cmd.Parameters.AddWithValue("u", userId);

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<string?> FindSuccessorAsync(string groupId, string excludingUserId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT user_id FROM group_members WHERE group_id = @g AND user_id <> @x AND role IN (1, 2) " +
            "ORDER BY role, updated_at LIMIT 1");
        cmd.Parameters.AddWithValue("g", groupId);
        cmd.Parameters.AddWithValue("x", excludingUserId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private const string UpsertRoleSql =
        "INSERT INTO group_members (group_id, user_id, role) VALUES (@g, @u, @r) " +
        "ON CONFLICT (group_id, user_id) DO UPDATE SET role = @r, updated_at = now()";
}

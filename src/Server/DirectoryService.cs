using NodaTime;

namespace SchoolBell.Server;

public sealed record GroupRow(int Id, string Name, int MemberCount);

/// Admin directory: groups + memberships + school settings (timezone, server name).
public sealed class DirectoryService
{
    private readonly Db _db;

    public DirectoryService(Db db) => _db = db;

    public (int Status, string? Detail, GroupRow? Group) CreateGroup(string actor, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            return (400, "group name required (max 64 chars)", null);
        using var conn = _db.Open();
        try
        {
            var id = conn.ScalarInt("INSERT INTO groups(name) VALUES ($n) RETURNING id", ("$n", name));
            Audit.Add(_db, actor, "group.create", name);
            return (201, null, new GroupRow(id, name, 0));
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19)
        {
            return (400, "group name must be unique", null);
        }
    }

    public List<GroupRow> ListGroups()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.id, g.name, COUNT(gm.device_id) FROM groups g
            LEFT JOIN group_members gm ON gm.group_id = g.id GROUP BY g.id ORDER BY g.id
            """;
        var list = new List<GroupRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new GroupRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    public (int Status, string? Detail) DeleteGroup(string actor, int id)
    {
        using var conn = _db.Open();
        if (conn.Run("DELETE FROM groups WHERE id = $id", ("$id", id)) == 0)
            return (404, "group not found");
        Audit.Add(_db, actor, "group.delete", $"id={id}");
        return (200, null);
    }

    public (int Status, string? Detail) AddMember(string actor, int groupId, string deviceId)
    {
        using var conn = _db.Open();
        if (!conn.Exists("SELECT COUNT(*) FROM groups WHERE id = $g", ("$g", groupId)))
            return (404, "group not found");
        if (!conn.Exists("SELECT COUNT(*) FROM devices WHERE device_id = $d", ("$d", deviceId)))
            return (404, "device not found");
        conn.Run("INSERT OR IGNORE INTO group_members(device_id, group_id) VALUES ($d, $g)",
            ("$d", deviceId), ("$g", groupId));
        Audit.Add(_db, actor, "group.member.add", $"group={groupId} device={deviceId}");
        return (200, null);
    }

    public (int Status, string? Detail) RemoveMember(string actor, int groupId, string deviceId)
    {
        using var conn = _db.Open();
        if (conn.Run("DELETE FROM group_members WHERE group_id = $g AND device_id = $d",
                ("$g", groupId), ("$d", deviceId)) == 0)
            return (404, "membership not found");
        Audit.Add(_db, actor, "group.member.remove", $"group={groupId} device={deviceId}");
        return (200, null);
    }

    public List<string> Members(int groupId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id FROM group_members WHERE group_id = $g ORDER BY device_id";
        cmd.Parameters.AddWithValue("$g", groupId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public Dictionary<string, string> GetSettings()
    {
        using var conn = _db.Open();
        return new Dictionary<string, string>
        {
            ["school_tz"] = Db.GetSetting(conn, "school_tz") ?? "",
            ["server_name"] = Db.GetSetting(conn, "server_name") ?? "",
            ["setup_completed"] = conn.Exists("SELECT COUNT(*) FROM users") ? "1" : "0",
        };
    }

    public (int Status, string? Detail) UpdateSettings(string actor, string? schoolTz, string? serverName)
    {
        using var conn = _db.Open();
        if (schoolTz is not null)
        {
            if (schoolTz.Length == 0 || DateTimeZoneProviders.Tzdb.GetZoneOrNull(schoolTz) is null)
                return (400, "school_tz must be a valid IANA timezone id");
            Db.SetSetting(conn, "school_tz", schoolTz);
        }
        if (!string.IsNullOrWhiteSpace(serverName))
            Db.SetSetting(conn, "server_name", serverName.Trim());
        Audit.Add(_db, actor, "settings.update", $"school_tz={schoolTz ?? "(unchanged)"}");
        return (200, null);
    }
}

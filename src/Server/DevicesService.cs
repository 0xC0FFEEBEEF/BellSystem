using System.Text.Json;
using System.Text.Json.Nodes;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record DeviceRow(string DeviceId, string Name, string Label, string Status, bool Revoked,
    long FirstSeen, long? LastSeen, long AcceptedRevision, long CacheRevision, long? CacheExpiresAt,
    List<long> Groups, bool Connected, string State, JsonNode? KeepAwake, string? ComputerName, string? ActiveUser,
    JsonNode? StatusReport = null, string? ClientVersion = null, string? Room = null, JsonNode? Update = null);

public sealed record DevicePage(int Total, int Page, int PageSize, List<DeviceRow> Items);

/// Device status ingest + dashboard listing with labels and derived states (PROTOCOL.md §4, plan §5).
public sealed class DevicesService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly TimeProvider _time;
    private readonly ControlRegistry _registry;

    public DevicesService(Db db, ServerConfig cfg, TimeProvider time, ControlRegistry registry)
    {
        _db = db; _cfg = cfg; _time = time; _registry = registry;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public void UpdateStatus(string deviceId, DeviceStatusPayload p, string rawJson)
    {
        using var conn = _db.Open();
        conn.Run("""
            UPDATE devices SET computer_name = $cn, active_user = $au, active_sid = $as,
                   status_json = $sj, cache_revision = $cr, cache_expires_at = $ce, last_seen = $t
            WHERE device_id = $d
            """,
            ("$cn", p.ComputerName), ("$au", p.ActiveUser), ("$as", p.ActiveUserSid),
            ("$sj", rawJson), ("$cr", p.CacheRevision), ("$ce", p.CacheExpiresAt),
            ("$t", NowMs), ("$d", deviceId));
        if (p.KeepAwake is not null)
            conn.Run("UPDATE devices SET keep_awake_json = $k WHERE device_id = $d",
                ("$k", JsonSerializer.Serialize(p.KeepAwake, ControlEnvelope.Json)), ("$d", deviceId));
    }

    public void UpdateAck(string deviceId, ScheduleAckPayload ack)
    {
        using var conn = _db.Open();
        conn.Run("""
            UPDATE devices SET accepted_revision = MAX(accepted_revision, $r),
                   cache_revision = MAX(cache_revision, $r), cache_expires_at = $e, last_seen = $t
            WHERE device_id = $d
            """,
            ("$r", ack.Revision), ("$e", ack.ExpiresAt), ("$t", NowMs), ("$d", deviceId));
    }

    public void UpdateCursor(string deviceId, string cursor)
    {
        using var conn = _db.Open();
        conn.Run("UPDATE devices SET last_cursor = $c, last_seen = $t WHERE device_id = $d",
            ("$c", cursor), ("$t", NowMs), ("$d", deviceId));
    }

    public HashSet<long> GroupsOf(string deviceId)
    {
        using var conn = _db.Open();
        return GroupsOf(conn, deviceId);
    }

    public static HashSet<long> GroupsOf(Microsoft.Data.Sqlite.SqliteConnection conn, string deviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_id FROM group_members WHERE device_id = $d";
        cmd.Parameters.AddWithValue("$d", deviceId);
        var set = new HashSet<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    public List<(string DeviceId, string Name)> NonRevokedDevices()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id, name FROM devices WHERE revoked = 0 ORDER BY device_id";
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// Client version and advertised receive limit per device (limit null = a client before 0.9.5).
    public Dictionary<string, (string? Version, int? MaxBytes)> MessageLimits()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id, client_version, max_message_bytes FROM devices";
        var map = new Dictionary<string, (string?, int?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2));
        return map;
    }

    public List<(string DeviceId, long AcceptedRevision)> AllDeviceRevisions()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id, accepted_revision FROM devices ORDER BY device_id";
        var list = new List<(string, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    }

    public DevicePage List(string? q, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);
        using var conn = _db.Open();

        string where = "";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            where = """
                WHERE (d.computer_name LIKE $q COLLATE NOCASE OR d.active_user LIKE $q COLLATE NOCASE
                       OR d.name LIKE $q COLLATE NOCASE OR d.device_id = $q2
                       OR EXISTS (SELECT 1 FROM group_members gm JOIN groups g ON g.id = gm.group_id
                                  WHERE gm.device_id = d.device_id AND g.name LIKE $q COLLATE NOCASE))
                """;
            ps.Add(("$q", $"%{q}%"));
            ps.Add(("$q2", q));
        }

        var total = Convert.ToInt32(conn.Scalar($"SELECT COUNT(*) FROM devices d {where}", ps.ToArray()));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT device_id FROM devices d {where} ORDER BY device_id LIMIT $l OFFSET $o";
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("$l", pageSize);
        cmd.Parameters.AddWithValue("$o", (page - 1) * pageSize);
        var ids = new List<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) ids.Add(r.GetString(0));

        var items = new List<DeviceRow>();
        foreach (var id in ids) items.Add(BuildRow(conn, id));
        return new DevicePage(total, page, pageSize, items);
    }

    public DeviceRow? Get(string deviceId)
    {
        using var conn = _db.Open();
        return conn.Exists("SELECT COUNT(*) FROM devices WHERE device_id = $d", ("$d", deviceId))
            ? BuildRow(conn, deviceId) : null;
    }

    private DeviceRow BuildRow(Microsoft.Data.Sqlite.SqliteConnection conn, string deviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, revoked, status, first_seen, last_seen, accepted_revision, cache_revision,
                   cache_expires_at, computer_name, active_user, status_json, keep_awake_json,
                   client_version, room, update_json
            FROM devices WHERE device_id = $d
            """;
        cmd.Parameters.AddWithValue("$d", deviceId);
        using var r = cmd.ExecuteReader();
        r.Read();
        var name = r.GetString(0);
        var revoked = r.GetInt32(1) == 1;
        var status = r.GetString(2);
        var firstSeen = r.GetInt64(3);
        long? lastSeen = r.IsDBNull(4) ? null : r.GetInt64(4);
        var accepted = r.GetInt64(5);
        var cacheRev = r.GetInt64(6);
        long? cacheExp = r.IsDBNull(7) ? null : r.GetInt64(7);
        var computerName = r.IsDBNull(8) ? null : r.GetString(8);
        var activeUser = r.IsDBNull(9) ? null : r.GetString(9);
        var statusJson = r.IsDBNull(10) ? null : r.GetString(10);
        var keepAwakeJson = r.IsDBNull(11) ? null : r.GetString(11);
        var clientVersion = r.IsDBNull(12) ? null : r.GetString(12);
        var room = r.IsDBNull(13) ? null : r.GetString(13);
        var updateJson = r.IsDBNull(14) ? null : r.GetString(14);
        r.Close();

        var groups = GroupsOf(conn, deviceId).ToList();
        var label = activeUser is not null
            ? $"{activeUser} ({computerName ?? name})"
            : $"No signed-in user ({computerName ?? name})";

        var connected = !revoked && _registry.Lookup(deviceId) is not null;
        var ageMs = lastSeen is null ? long.MaxValue : NowMs - lastSeen.Value;
        var state = DeriveState(statusJson, connected, revoked, ageMs);

        JsonNode? keepAwake = keepAwakeJson is null ? null : TryParse(keepAwakeJson);
        if (room is not null) label = $"{room} — {label}";
        return new DeviceRow(deviceId, name, label, status, revoked, firstSeen, lastSeen, accepted,
            cacheRev, cacheExp, groups, connected, state, keepAwake, computerName, activeUser,
            statusJson is null ? null : TryParse(statusJson), clientVersion, room,
            updateJson is null ? null : TryParse(updateJson));
    }

    private static JsonNode? TryParse(string json)
    {
        try { return JsonNode.Parse(json); } catch (JsonException) { return null; }
    }

    /// Dashboard states from PROTOCOL.md §4/plan §5. A green connection never implies audible sound.
    private string DeriveState(string? statusJson, bool connected, bool revoked, long ageMs)
    {
        if (revoked) return "revoked";
        DeviceStatusPayload? s = null;
        if (statusJson is not null)
        {
            try { s = JsonSerializer.Deserialize<DeviceStatusPayload>(statusJson, ControlEnvelope.Json); }
            catch (JsonException) { }
        }
        var staleThreshold = _cfg.StaleAfterSeconds * 1000L;
        if (!connected) return ageMs > staleThreshold ? "offline" : "stale";
        // connected = the socket answered heartbeats within the last 30 s (KeepAliveTimeout)
        if (s is null) return "connected";
        if (s.LastPlayback is not null && s.LastPlayback.State == ReceiptState.Failed) return "playback_failed";
        if (s.RendererReason == "missing_output" || (s.Renderer == "ready" && !s.OutputPresent)) return "missing_speaker";
        if (s.Renderer != "ready") return "no_renderer";
        if (!s.OutputPresent) return "missing_speaker";
        if (s.MasterMuted || s.SessionMuted) return "muted";
        if (s.LastPlayback is not null && s.LastPlayback.State == ReceiptState.Started) return "playing";
        return "ready_to_play";
    }
}

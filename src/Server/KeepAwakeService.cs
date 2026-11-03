using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record KeepAwakePolicy(string PolicyId, int Id, string Scope, string? TargetId, string Mode,
    long ExpiresAt, bool DisplayOff, bool Released, long CreatedAt, string CreatedBy);

public sealed record KeepAwakePolicyView(KeepAwakePolicy Policy, int DesiredCount, int AckCount, bool Expired);

/// Keep-awake policies (PROTOCOL.md §9): absolute expiry, pushed via keepawake.set/.clear outbox
/// events; acknowledgements arrive on the control channel (keepawake.ack). Expired policies are
/// never re-sent.
public sealed class KeepAwakeService
{
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly OutboxService _outbox;
    private readonly DevicesService _devices;

    public KeepAwakeService(Db db, TimeProvider time, OutboxService outbox, DevicesService devices)
    {
        _db = db; _time = time; _outbox = outbox; _devices = devices;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public Outcome Create(string actor, string scope, string? targetId, int? durationMinutes,
        long? untilUtcMs, bool displayOff)
    {
        if (scope is not ("device" or "group" or "all"))
            return new(400, "scope must be device, group or all");
        if (durationMinutes is null && untilUtcMs is null)
            return new(400, "provide duration_minutes or until_utc (unix ms)");
        if (durationMinutes is not null && (durationMinutes < 1 || durationMinutes > 24 * 14))
            return new(400, "duration_minutes must be 1-2016");
        long expiresAt = untilUtcMs ?? NowMs + durationMinutes!.Value * 60_000L;
        if (expiresAt <= NowMs) return new(400, "expiry must be in the future");
        if (untilUtcMs is not null && untilUtcMs > NowMs + 24 * 14 * 3600_000L)
            return new(400, "until_utc is too far in the future (max 14 days)");

        using var conn = _db.Open();
        if (scope == "device" && !conn.Exists("SELECT COUNT(*) FROM devices WHERE device_id = $t", ("$t", targetId)))
            return new(400, "unknown device_id");
        if (scope == "group" && !conn.Exists("SELECT COUNT(*) FROM groups WHERE id = $t", ("$t", targetId)))
            return new(400, "unknown group id");
        if (scope != "all" && string.IsNullOrWhiteSpace(targetId))
            return new(400, "target_id is required for device/group scope");

        var policyId = Guid.NewGuid().ToString();
        var id = conn.ScalarInt("""
            INSERT INTO keepawake_policies(policy_id, scope, target_id, mode, expires_at, display_off, created_by, created_at)
            VALUES ($p, $s, $t, 'until', $e, $d, $c, $n) RETURNING id
            """,
            ("$p", policyId), ("$s", scope), ("$t", targetId), ("$e", expiresAt),
            ("$d", displayOff ? 1 : 0), ("$c", actor), ("$n", NowMs));

        var policy = Load(policyId)!;
        foreach (var (deviceId, _) in AffectedDevices(policy))
            EnqueueSet(deviceId, policy);
        Audit.Add(_db, actor, "keepawake.create", $"policy={policyId} scope={scope} target={targetId} expires_at={expiresAt}");
        _ = id;
        return new(201, null);
    }

    public Outcome Release(string actor, string policyId)
    {
        var policy = Load(policyId);
        if (policy is null) return new(404, "keep-awake policy not found");
        if (policy.Released) return new(409, "policy already released");
        using var conn = _db.Open();
        conn.Run("UPDATE keepawake_policies SET released = 1 WHERE policy_id = $p", ("$p", policyId));
        var env = ControlEnvelope.Create(Protocol.TKeepAwakeClear,
            PayloadSerializer.Node(new KeepAwakeClearPayload(policyId)));
        foreach (var (deviceId, _) in AffectedDevices(policy))
            _outbox.Enqueue(deviceId, env);
        Audit.Add(_db, actor, "keepawake.release", $"policy={policyId}");
        return new(200, null);
    }

    public List<KeepAwakePolicyView> List()
    {
        var now = NowMs;
        var views = new List<KeepAwakePolicyView>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT policy_id, id, scope, target_id, mode, expires_at, display_off, released, created_at, created_by
            FROM keepawake_policies ORDER BY id DESC
            """;
        var policies = new List<KeepAwakePolicy>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                policies.Add(new KeepAwakePolicy(r.GetString(0), r.GetInt32(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt64(5),
                    r.GetInt32(6) == 1, r.GetInt32(7) == 1, r.GetInt64(8), r.GetString(9)));
        foreach (var p in policies)
        {
            var desired = AffectedDevices(p).Count;
            var acks = Convert.ToInt32(conn.Scalar(
                "SELECT COUNT(*) FROM keepawake_acks WHERE policy_id = $p AND applied = 1", ("$p", p.PolicyId)));
            views.Add(new KeepAwakePolicyView(p, desired, acks, p.ExpiresAt <= now));
        }
        return views;
    }

    public KeepAwakePolicy? Load(string policyId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT policy_id, id, scope, target_id, mode, expires_at, display_off, released, created_at, created_by
            FROM keepawake_policies WHERE policy_id = $p
            """;
        cmd.Parameters.AddWithValue("$p", policyId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new KeepAwakePolicy(r.GetString(0), r.GetInt32(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt64(5),
            r.GetInt32(6) == 1, r.GetInt32(7) == 1, r.GetInt64(8), r.GetString(9));
    }

    public void RecordAck(string deviceId, KeepAwakeAckPayload ack)
    {
        using var conn = _db.Open();
        conn.Run("""
            INSERT INTO keepawake_acks(policy_id, device_id, applied, applied_at)
            VALUES ($p, $d, $a, $t)
            ON CONFLICT(policy_id, device_id) DO UPDATE SET applied = $a, applied_at = $t
            """,
            ("$p", ack.PolicyId), ("$d", deviceId), ("$a", ack.Applied ? 1 : 0), ("$t", ack.AppliedAt));
        conn.Run("UPDATE devices SET keep_awake_json = $k WHERE device_id = $d",
            ("$k", System.Text.Json.JsonSerializer.Serialize(new { desired = ack.Applied, applied = ack.Applied, expires_at = 0L }, ControlEnvelope.Json)),
            ("$d", deviceId));
        Audit.Add(_db, $"device:{deviceId}", "keepawake.ack", $"policy={ack.PolicyId} applied={ack.Applied}");
    }

    public List<KeepAwakePolicy> ActivePoliciesFor(string deviceId)
    {
        var now = NowMs;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT policy_id FROM keepawake_policies
            WHERE released = 0 AND expires_at > $now
              AND (scope = 'all'
                   OR (scope = 'device' AND target_id = $d)
                   OR (scope = 'group' AND EXISTS (SELECT 1 FROM group_members gm
                        WHERE gm.device_id = $d AND gm.group_id = CAST(target_id AS INTEGER))))
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$d", deviceId);
        var ids = new List<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) ids.Add(r.GetString(0));
        return ids.Select(Load).Where(p => p is not null).Select(p => p!).ToList();
    }

    public bool HasPendingSet(string deviceId, string policyId)
    {
        using var conn = _db.Open();
        return conn.Exists("""
            SELECT COUNT(*) FROM outbox WHERE device_id = $d AND event_json LIKE $pat
            """,
            ("$d", deviceId), ("$pat", $"%\"type\":\"{Protocol.TKeepAwakeSet}\"%\"policy_id\":\"{policyId}\"%"));
    }

    public void EnqueueSet(string deviceId, KeepAwakePolicy policy)
    {
        if (policy.ExpiresAt <= NowMs) return; // expired policies are never re-sent
        var env = ControlEnvelope.Create(Protocol.TKeepAwakeSet,
            PayloadSerializer.Node(new KeepAwakeSetPayload(policy.PolicyId, policy.ExpiresAt, policy.DisplayOff)));
        _outbox.Enqueue(deviceId, env);
    }

    public List<(string DeviceId, string Name)> AffectedDevices(KeepAwakePolicy p)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = p.Scope switch
        {
            "all" => "SELECT device_id, name FROM devices WHERE revoked = 0 ORDER BY device_id",
            "device" => "SELECT device_id, name FROM devices WHERE revoked = 0 AND device_id = $t",
            _ => """
                SELECT d.device_id, d.name FROM devices d JOIN group_members gm ON gm.device_id = d.device_id
                WHERE d.revoked = 0 AND gm.group_id = $t ORDER BY d.device_id
                """,
        };
        if (p.Scope != "all") cmd.Parameters.AddWithValue("$t", p.TargetId);
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }
}

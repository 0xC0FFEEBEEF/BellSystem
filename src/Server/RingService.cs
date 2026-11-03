using System.Text.Json;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record RingOutcome(int Status, string? Detail, string? RingId = null, int DeviceCount = 0, string? TargetLabel = null);

public sealed record RingDeviceView(string DeviceId, string Label, string State, string? ErrorCode, long UpdatedAt);
public sealed record RingView(string RingId, string RequestedBy, long CreatedAt, long ExpiresAt, string BellName,
    string SoundId, int DurationS, string TargetLabel, Dictionary<string, int> Counts, List<RingDeviceView> Devices);

/// "Ring now" (PROTOCOL §13 bell.ring): an immediate bell to all endpoints, groups or chosen
/// endpoints. The audience is resolved server-side and frozen; each device claims the ring id in
/// its durable ledger (no double ring) and never rings after expires_at (no late ring after a
/// reconnect). Results come only from device receipts.
public sealed partial class AnnouncementsService
{
    private static readonly string[] RingStateRank =
        ["offered", "ready", "started", "unknown", "expired", "suppressed", "failed", "completed"];

    public RingOutcome RingNow(CurrentUser user, TargetScope target, string? soundId, string? bellName, int? durationS)
    {
        var (gate, gateDetail) = _leases.GateTarget(user, target);
        if (gate != 200) return new(gate, gateDetail?.Replace("make announcements", "ring bells"));
        soundId = string.IsNullOrWhiteSpace(soundId) ? "std-bell" : soundId.Trim();
        int duration = Math.Clamp(durationS ?? 0, 0, 60);
        string name = string.IsNullOrWhiteSpace(bellName) ? "Bell (rung by the office)" : bellName.Trim();
        if (name.Length > 80) name = name[..80];

        long now = NowMs;
        var ringId = "ring-" + Guid.NewGuid().ToString("N");
        long expiresAt = now + Protocol.ManualRingTtlSeconds * 1000L;
        List<(string DeviceId, string Label)> targets;
        string targetLabel, sha;
        using (var conn = _db.Open())
        {
            sha = conn.Scalar("SELECT sha256 FROM sound_assets WHERE id = $i", ("$i", soundId)) as string ?? "";
            if (sha == "") return new(404, $"sound '{soundId}' does not exist");
            (targets, targetLabel) = ResolveTarget(conn, target);
            if (targets.Count == 0)
                return new(409, "the resolved audience is empty (no matching enrolled, non-revoked devices)");
            using var tx = conn.BeginTransaction();
            conn.Run(tx, """
                INSERT INTO bell_rings(ring_id, requested_by, created_at, expires_at, bell_name, sound_id, sound_sha256,
                                       duration_s, volume_policy, target_json, target_label)
                VALUES ($r, $u, $c, $e, $n, $s, $h, $d, 'announcement', $tj, $tl)
                """,
                ("$r", ringId), ("$u", user.Username), ("$c", now), ("$e", expiresAt), ("$n", name), ("$s", soundId),
                ("$h", sha), ("$d", duration), ("$tj", TalkLeaseService.WriteTarget(target)), ("$tl", targetLabel));
            foreach (var (deviceId, label) in targets)
                conn.Run(tx, """
                    INSERT INTO ring_results(ring_id, device_id, label, state, updated_at) VALUES ($r, $d, $l, 'offered', $t)
                    """, ("$r", ringId), ("$d", deviceId), ("$l", label), ("$t", now));
            tx.Commit();
            Audit.Add(conn, null, user.Username, "bell.ring",
                $"ring={ringId} sound={soundId} devices={targets.Count} target={targetLabel}");
        }

        var env = ControlEnvelope.Create(Protocol.TBellRing, PayloadSerializer.Node(new BellRingPayload(
            ringId, name, new SoundRef(soundId, sha, $"/api/sounds/{soundId}?sha={sha}"), duration, "announcement",
            now, expiresAt, user.Username)));
        foreach (var (deviceId, _) in targets) _outbox.Enqueue(deviceId, env);
        _log.LogInformation("ring {RingId} ('{Bell}', sound {Sound}) sent to {Count} device(s) by {User}",
            ringId, name, soundId, targets.Count, user.Username);
        return new(201, null, ringId, targets.Count, targetLabel);
    }

    /// Device receipt naming a ring id. Keeps the most final state (receipts can arrive in any order).
    public bool OnRingReceipt(string deviceId, string ringId, string state, string? errorCode)
    {
        using var conn = _db.Open();
        var current = conn.Scalar("SELECT state FROM ring_results WHERE ring_id = $r AND device_id = $d",
            ("$r", ringId), ("$d", deviceId)) as string;
        if (current is null) return false;
        if (Array.IndexOf(RingStateRank, state) < Array.IndexOf(RingStateRank, current) && current != "offered") return true;
        conn.Run("UPDATE ring_results SET state = $s, error_code = $e, updated_at = $t WHERE ring_id = $r AND device_id = $d",
            ("$s", state), ("$e", errorCode), ("$t", NowMs), ("$r", ringId), ("$d", deviceId));
        return true;
    }

    public List<RingView> RecentRings(int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
        long now = NowMs;
        using var conn = _db.Open();
        var rings = new List<RingView>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ring_id, requested_by, created_at, expires_at, bell_name, sound_id, duration_s, target_label
                FROM bell_rings ORDER BY created_at DESC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rings.Add(new RingView(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4),
                    r.GetString(5), r.GetInt32(6), r.GetString(7), [], []));
        }
        foreach (var ring in rings)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT device_id, label, state, error_code, updated_at FROM ring_results WHERE ring_id = $r ORDER BY label";
            cmd.Parameters.AddWithValue("$r", ring.RingId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var state = r.GetString(2);
                // honest display: a device that never answered within the window is "no_response", not delivered
                if (state is "offered" && now > ring.ExpiresAt + 20_000) state = "no_response";
                ring.Devices.Add(new RingDeviceView(r.GetString(0), r.GetString(1), state,
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4)));
            }
            foreach (var g in ring.Devices.GroupBy(d => d.State)) ring.Counts[g.Key] = g.Count();
        }
        return rings;
    }

    // ---------------------------------------------------------------- tray history push

    /// history.item to every device in the announcement's frozen audience. Text is included only
    /// for schoolwide announcements; targeted ones are office-only (plan §11).
    public void PushHistory(string announcementId)
    {
        var ann = Read(announcementId);
        if (ann is null) return;
        bool schoolwide = ann.Target.All == true;
        var t = Transcript(announcementId);
        string status = t?.Status switch { "completed" => "done", "failed" => "failed", _ => "pending" };
        string? text = schoolwide && status == "done" ? t?.Current : null;
        bool corrected = t?.CorrectedBy is not null;
        var env = ControlEnvelope.Create(Protocol.THistoryItem, PayloadSerializer.Node(new HistoryItemPayload(
            announcementId, ann.StartedAt ?? ann.CreatedAt, ann.Kind, ann.SourceLabel, schoolwide, status,
            text is { Length: > 8000 } ? text[..8000] : text, corrected)));
        foreach (var (deviceId, _) in SnapshotDevices(announcementId)) _outbox.Enqueue(deviceId, env);
    }

    /// Replay filter for rings: an expired ring is never replayed (the device would record it
    /// expired anyway); the result is marked expired for an offline device.
    private bool RingReplayOk(string deviceId, ControlEnvelope env)
    {
        var ring = PayloadSerializer.ToPayload<BellRingPayload>(env);
        if (ring is null) return false;
        if (ring.ExpiresAt > NowMs) return true;
        OnRingReceipt(deviceId, ring.RingId, ReceiptState.Expired, "offline_until_expiry");
        return false;
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record AnnouncementRow(string AnnouncementId, string Kind, string Status, string CreatedBy,
    long CreatedAt, long? StartedAt, long? WindowClosesAt, long? EndedAt, string? EndedBy, int MaxDurationS,
    long OfferExpiresAt, bool Chime, string TargetJson, string TargetLabel, string AudienceSnapshotId,
    string? LeaseId, string SourceLabel, string? RecordingPath, string? RecordingSha256, long? RecordingBytes,
    long? RecordingDurationMs, int? RecordingSampleRate, long FramesSent)
{
    public TargetScope Target => TalkLeaseService.ReadTarget(TargetJson);
    public bool IsLive => Kind == "live";
}

public sealed record AnnounceOutcome(int Status, string? Detail, AnnouncementRow? Announcement = null, int DeviceCount = 0);

/// Milestone S2 announcement orchestration (PROTOCOL.md §7, plan §6/§7/§9/§11):
/// talk lease -> server-side target resolution (audience snapshot frozen at start) ->
/// announcement.offer to every targeted device (control live send + durable outbox) ->
/// bounded preparation window collecting announcement.ready / announcement.reject ->
/// media.token (single-use OTP) to each READY device -> media relay -> announcement.end.
///
/// Honesty rules baked in here: a live offer is never replayed after it expires (an offline
/// device is reported `expired`, not "delivered"), socket delivery is recorded as `started`, and
/// `completed` only ever comes from the device's own receipt.
public sealed partial class AnnouncementsService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly LiveSettings _settings;
    private readonly TimeProvider _time;
    private readonly DevicesService _devices;
    private readonly OutboxService _outbox;
    private readonly ControlRegistry _registry;
    private readonly MediaRouter _media;
    private readonly TalkLeaseService _leases;
    private readonly ILogger<AnnouncementsService> _log;

    public AnnouncementsService(Db db, ServerConfig cfg, LiveSettings settings, TimeProvider time,
        DevicesService devices, OutboxService outbox, ControlRegistry registry, MediaRouter media,
        TalkLeaseService leases, ILogger<AnnouncementsService> log)
    {
        _db = db; _cfg = cfg; _settings = settings; _time = time; _devices = devices; _outbox = outbox;
        _registry = registry; _media = media; _leases = leases; _log = log;
    }

    public string AudioDir => Path.Combine(_cfg.DataDir, "audio");
    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- live session

    public AnnounceOutcome StartLive(CurrentUser user, TargetScope target, bool chime, string? leaseId)
    {
        var (gate, gateDetail) = _leases.GateTarget(user, target);
        if (gate != 200) return new(gate, gateDetail);

        var lease = leaseId is null ? _leases.ActiveLease() : _leases.Read(leaseId);
        if (lease is null) return new(404, "no such lease");
        if (lease.Status != "active") return new(410, $"lease expired ({lease.EndedReason ?? "expired"})");
        if (lease.Username != user.Username) return new(403, $"lease held by {lease.Username}");
        if (_leases.ActiveLease() is { } active && active.LeaseId != lease.LeaseId)
            return new(409, $"lease held by {active.Username} since {active.CreatedAtIso}");

        long now = NowMs;
        var snapshotId = Guid.NewGuid().ToString();
        var announcementId = Guid.NewGuid().ToString();
        long offerExpiresAt = now + _settings.LiveOfferTtlS * 1000L;
        long windowClosesAt = now + _settings.PreparationWindowS * 1000L;
        int maxDurationS = lease.MaxDurationS;

        List<(string DeviceId, string Label)> targets;
        string targetLabel;
        using (var conn = _db.Open())
        {
            (targets, targetLabel) = ResolveTarget(conn, target);
            if (targets.Count == 0)
                return new(409, "the resolved audience is empty (no matching enrolled, non-revoked devices)");

            conn.Run("""
                INSERT INTO announcements(announcement_id, kind, status, created_by, created_at, started_at,
                    window_closes_at, max_duration_s, offer_expires_at, chime, target_json, target_label,
                    audience_snapshot_id, lease_id, source_label)
                VALUES ($a, 'live', 'active', $u, $c, NULL, $w, $m, $o, $ch, $tj, $tl, $s, $l, $sl)
                """,
                ("$a", announcementId), ("$u", user.Username), ("$c", now), ("$w", windowClosesAt),
                ("$m", maxDurationS), ("$o", offerExpiresAt), ("$ch", chime ? 1 : 0),
                ("$tj", TalkLeaseService.WriteTarget(target)), ("$tl", targetLabel), ("$s", snapshotId),
                ("$l", lease.LeaseId), ("$sl", $"Office — {targetLabel}"));

            foreach (var (deviceId, label) in targets)
            {
                conn.Run("INSERT INTO announcement_devices(announcement_id, device_id, snapshot_id, label) VALUES ($a, $d, $s, $lb)",
                    ("$a", announcementId), ("$d", deviceId), ("$s", snapshotId), ("$lb", label));
                conn.Run("""
                    INSERT INTO delivery_results(announcement_id, device_id, state, error_code, source, updated_at)
                    VALUES ($a, $d, 'offered', NULL, 'server', $t)
                    ON CONFLICT(announcement_id, device_id, state) DO UPDATE SET updated_at = $t
                    """, ("$a", announcementId), ("$d", deviceId), ("$t", now));
            }
            Audit.Add(conn, null, user.Username, "announcement.live_start",
                $"announcement={announcementId} snapshot={snapshotId} devices={targets.Count} max_duration_s={maxDurationS} lease={lease.LeaseId}");
        }

        _leases.AttachAnnouncement(lease.LeaseId, announcementId);
        var offerEnv = OfferEnvelope(announcementId, "live", snapshotId, "live", offerExpiresAt, maxDurationS,
            chime, $"Office — {targetLabel}");
        foreach (var (deviceId, _) in targets) _outbox.Enqueue(deviceId, offerEnv);

        _log.LogInformation("live announcement {AnnouncementId} offered to {Count} device(s); ready window closes at {Window}",
            announcementId, targets.Count, DateTimeOffset.FromUnixTimeMilliseconds(windowClosesAt).ToString("o"));
        return new(201, null, Read(announcementId), targets.Count);
    }

    /// Close the bounded preparation window: devices that never answered are reported honestly
    /// (offline => expired, connected-but-silent => failed(ready_timeout)); ready devices get a
    /// media token and a bounded send queue.
    public int ClosePreparationWindows()
    {
        long now = NowMs;
        var due = new List<string>();
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT announcement_id FROM announcements
                WHERE status = 'active' AND kind = 'live' AND window_closes_at IS NOT NULL AND window_closes_at <= $n
                """;
            cmd.Parameters.AddWithValue("$n", now);
            using var r = cmd.ExecuteReader();
            while (r.Read()) due.Add(r.GetString(0));
        }

        foreach (var annId in due)
        {
            var ready = new List<(string DeviceId, string Label)>();
            int failedTimeout = 0, expiredOffline = 0;
            using (var conn = _db.Open())
            {
                var snapshot = SnapshotDevices(conn, annId);
                foreach (var (deviceId, _) in snapshot)
                {
                    bool answered = conn.Exists("""
                        SELECT COUNT(*) FROM delivery_results
                        WHERE announcement_id = $a AND device_id = $d AND state IN ('ready', 'failed')
                        """, ("$a", annId), ("$d", deviceId));
                    if (answered) continue;
                    if (_registry.Lookup(deviceId) is not null)
                    {
                        RecordDelivery(conn, null, annId, deviceId, ReceiptState.Failed, "ready_timeout", "server");
                        failedTimeout++;
                    }
                    else
                    {
                        RecordDelivery(conn, null, annId, deviceId, ReceiptState.Expired, "offer_expired", "server");
                        expiredOffline++;
                    }
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT device_id FROM delivery_results WHERE announcement_id = $a AND state = 'ready'
                    """;
                cmd.Parameters.AddWithValue("$a", annId);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) ready.Add((r.GetString(0), ""));

                conn.Run("UPDATE announcements SET window_closes_at = NULL, started_at = $t WHERE announcement_id = $a",
                    ("$t", now), ("$a", annId));
            }

            foreach (var (deviceId, _) in ready)
            {
                _media.OpenStream(annId, deviceId);
                IssueMediaToken(annId, deviceId);
            }
            _log.LogInformation("announcement {AnnouncementId} ready window closed: {Ready} ready, {Timeout} failed(ready_timeout), {Expired} expired(offline)",
                annId, ready.Count, failedTimeout, expiredOffline);
        }
        return due.Count;
    }

    /// Max-duration watchdog + recorded offer expiry (PROTOCOL.md §7: lease/max-duration ends the
    /// announcement; plan §9: recorded offers live until offer_expires_at).
    public int WatchdogSweep()
    {
        long now = NowMs;
        var due = new List<(string Id, string EndedBy)>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT announcement_id, kind FROM announcements WHERE status = 'active'
              AND ((kind = 'live' AND created_at + max_duration_s * 1000 <= $n)
                   OR (kind = 'recorded' AND offer_expires_at <= $n))
            """;
        cmd.Parameters.AddWithValue("$n", now);
        using (var r = cmd.ExecuteReader())
            while (r.Read()) due.Add((r.GetString(0), r.GetString(1) == "live" ? "timeout" : "expired"));

        foreach (var (id, endedBy) in due)
        {
            _log.LogInformation("announcement {AnnouncementId} ended by the watchdog ({EndedBy})", id, endedBy);
            End(id, endedBy, "server");
        }
        return due.Count;
    }

    // ---------------------------------------------------------------- recorded session

    public AnnounceOutcome StartRecorded(CurrentUser user, TargetScope target, bool chime, byte[] upload)
    {
        var (gate, gateDetail) = _leases.GateTarget(user, target);
        if (gate != 200) return new(gate, gateDetail);
        if (upload.Length > LiveSettings.RecordedMaxBytes)
            return new(400, $"recording is {upload.Length} bytes; the limit is {LiveSettings.RecordedMaxBytes} bytes (25 MB)");

        var (parsed, invalid) = Wav.ValidateUpload(upload);
        if (parsed is null) return new(400, invalid!);

        var inspect = parsed.Inspect;
        int srcRate = inspect.SampleRate;
        byte[] pcm = parsed.Pcm(upload).ToArray();
        byte[] mono = Wav.ResampleMono16Linear(pcm, srcRate, Wav.TargetSampleRate);
        bool resampled = srcRate != Wav.TargetSampleRate;

        var announcementId = Guid.NewGuid().ToString();
        var snapshotId = Guid.NewGuid().ToString();
        long now = NowMs;
        long offerExpiresAt = now + _settings.RecordedOfferTtlS * 1000L;
        var storedPath = Path.Combine(AudioDir, $"rec-{announcementId}.wav");
        var final = Wav.WriteMono16(storedPath, mono, Wav.TargetSampleRate);

        List<(string DeviceId, string Label)> targets;
        string targetLabel;
        using (var conn = _db.Open())
        {
            (targets, targetLabel) = ResolveTarget(conn, target);
            if (targets.Count == 0)
            {
                try { File.Delete(storedPath); } catch (IOException) { }
                return new(409, "the resolved audience is empty (no matching enrolled, non-revoked devices)");
            }
            conn.Run("""
                INSERT INTO announcements(announcement_id, kind, status, created_by, created_at, started_at,
                    max_duration_s, offer_expires_at, chime, target_json, target_label, audience_snapshot_id,
                    source_label, recording_path, recording_sha256, recording_bytes, recording_duration_ms,
                    recording_sample_rate)
                VALUES ($a, 'recorded', 'active', $u, $c, $st, $m, $o, $ch, $tj, $tl, $s, $sl, $rp, $rs, $rb, $rd, $rr)
                """,
                ("$a", announcementId), ("$u", user.Username), ("$c", now), ("$st", now),
                ("$m", (int)Math.Ceiling(final.DurationMs / 1000.0)), ("$o", offerExpiresAt), ("$ch", chime ? 1 : 0),
                ("$tj", TalkLeaseService.WriteTarget(target)), ("$tl", targetLabel), ("$s", snapshotId),
                ("$sl", $"Office — {targetLabel}"), ("$rp", storedPath), ("$rs", final.Sha256),
                ("$rb", final.Bytes), ("$rd", final.DurationMs), ("$rr", Wav.TargetSampleRate));

            foreach (var (deviceId, label) in targets)
            {
                conn.Run("INSERT INTO announcement_devices(announcement_id, device_id, snapshot_id, label) VALUES ($a, $d, $s, $lb)",
                    ("$a", announcementId), ("$d", deviceId), ("$s", snapshotId), ("$lb", label));
                conn.Run("""
                    INSERT INTO delivery_results(announcement_id, device_id, state, error_code, source, updated_at)
                    VALUES ($a, $d, 'offered', NULL, 'server', $t)
                    ON CONFLICT(announcement_id, device_id, state) DO UPDATE SET updated_at = $t
                    """, ("$a", announcementId), ("$d", deviceId), ("$t", now));
            }
            Audit.Add(conn, null, user.Username, "announcement.recorded_upload",
                $"announcement={announcementId} devices={targets.Count} bytes={final.Bytes} sha256={final.Sha256} " +
                $"duration_ms={final.DurationMs} source_rate={srcRate} resampled={(resampled ? 1 : 0)}");
        }

        // Recorded offers stay offerable until offer_expires_at: the offer lives in the durable
        // outbox and is re-offered on resync while it is still valid (ControlHub.OutboxReplayOk).
        var offerEnv = OfferEnvelope(announcementId, "recorded", snapshotId, "normal", offerExpiresAt,
            (int)Math.Ceiling(final.DurationMs / 1000.0), chime, $"Office — {targetLabel}");
        foreach (var (deviceId, _) in targets) _outbox.Enqueue(deviceId, offerEnv);

        _log.LogInformation("recorded announcement {AnnouncementId}: {Bytes} bytes stored (sha256 {Sha}), offered to {Count} device(s), offer valid until {Expiry}",
            announcementId, final.Bytes, final.Sha256[..12], targets.Count,
            DateTimeOffset.FromUnixTimeMilliseconds(offerExpiresAt).ToString("o"));
        return new(201, null, Read(announcementId), targets.Count);
    }

    // ---------------------------------------------------------------- device responses

    /// announcement.ready from the device (ControlHub dispatch). Returns false for an unknown
    /// announcement/device so the caller can treat it as a protocol violation.
    public bool RecordReady(string deviceId, string announcementId, string? outputDevice)
    {
        var ann = Read(announcementId);
        if (ann is null || !IsTargeted(announcementId, deviceId)) return false;
        RecordDelivery(announcementId, deviceId, ReceiptState.Ready, null, "device");
        if (ann.Status == "active" && (!ann.IsLive || ann.StartedAt is not null))
        {
            // window already closed (or a recorded offer): the device may start receiving now
            _media.OpenStream(announcementId, deviceId, ann.IsLive ? null : ann.RecordingPath);
            IssueMediaToken(announcementId, deviceId);
        }
        return true;
    }

    public bool RecordReject(string deviceId, string announcementId, string reason)
    {
        if (Read(announcementId) is null || !IsTargeted(announcementId, deviceId)) return false;
        RecordDelivery(announcementId, deviceId, ReceiptState.Failed, reason is null or "" ? "rejected" : reason, "device");
        _log.LogInformation("device {DeviceId} rejected announcement {AnnouncementId} ({Reason})",
            deviceId, announcementId, reason);
        return true;
    }

    /// A device receipt (type "receipt") that names an announcement.
    public bool OnDeviceReceipt(string deviceId, string refId, string state, string? errorCode)
    {
        if (Read(refId) is null || !IsTargeted(refId, deviceId)) return false;
        RecordDelivery(refId, deviceId, state, errorCode, "device");
        return true;
    }

    // MediaRouter callbacks (wired in Program.cs)
    public void RecordStreamStarted(string announcementId, string deviceId) =>
        RecordDelivery(announcementId, deviceId, ReceiptState.Started, null, "server");

    public void RecordQueueOverflow(string announcementId, string deviceId)
    {
        RecordDelivery(announcementId, deviceId, ReceiptState.Failed, "receive_queue_overflow", "server");
        _media.CloseDeviceStream(announcementId, deviceId, "receive_queue_overflow");
    }

    /// Socket closed before the final frame was written: that is a failed delivery, not a
    /// completed one. A device that never reports `completed` is never counted as audible.
    public void RecordMediaSocketClosed(string announcementId, string deviceId, string reason)
    {
        if (reason == "completed") return;                 // last frame delivered; audible still unknown
        if (reason == "receive_queue_overflow") return;    // already recorded
        RecordDelivery(announcementId, deviceId, ReceiptState.Failed, reason, "server");
    }

    // ---------------------------------------------------------------- media tokens

    /// PROTOCOL.md §7 binding: single-use OTP issued on the CONTROL channel, bound to
    /// (device_id, announcement_id), stored hashed only, TTL 10 s by default.
    public string IssueMediaToken(string announcementId, string deviceId)
    {
        var token = Util.RandomHex(32);                     // 32 random bytes, hex
        long now = NowMs;
        long expiresAt = now + _settings.MediaTokenTtlS * 1000L;
        using (var conn = _db.Open())
            conn.Run("""
                INSERT INTO media_tokens(announcement_id, device_id, token_hash, expires_at, issued_at, used)
                VALUES ($a, $d, $h, $e, $i, 0)
                ON CONFLICT(announcement_id, device_id) DO UPDATE SET token_hash = $h, expires_at = $e,
                     issued_at = $i, used = 0, used_at = NULL
                """,
                ("$a", announcementId), ("$d", deviceId), ("$h", Util.Sha256Hex(token)),
                ("$e", expiresAt), ("$i", now));

        var env = ControlEnvelope.Create(Protocol.TMediaToken,
            PayloadSerializer.Node(new MediaTokenPayload(announcementId, token, expiresAt)));
        _outbox.Enqueue(deviceId, env);
        return token;
    }

    /// Atomic single-use redemption: one UPDATE claim, constant-time hash comparison happens in
    /// SQL via the stored hash lookup; the raw token is never stored or logged.
    public bool ConsumeMediaToken(string announcementId, string deviceId, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return false;
        long now = NowMs;
        using var conn = _db.Open();
        int claimed = conn.Run("""
            UPDATE media_tokens SET used = 1, used_at = $n
            WHERE announcement_id = $a AND device_id = $d AND token_hash = $h AND used = 0 AND expires_at > $n
            """,
            ("$a", announcementId), ("$d", deviceId), ("$h", Util.Sha256Hex(token)), ("$n", now));
        if (claimed == 1) return true;
        _log.LogWarning("media token rejected for device {DeviceId} announcement {AnnouncementId} (unknown token, wrong device, expired or already used)",
            deviceId, announcementId);
        return false;
    }

    // ---------------------------------------------------------------- outbox replay filter

    /// Resync replay policy (PROTOCOL.md §7/§10): live offers and media tokens are never replayed
    /// once expired — stale live audio is never replayed, and the device is reported `expired`.
    /// Recorded offers ARE re-offered while offer_expires_at is still in the future.
    public bool OutboxReplayOk(string deviceId, ControlEnvelope env)
    {
        if (env.Type == Protocol.TAnnouncementOffer)
        {
            var payload = PayloadSerializer.ToPayload<AnnouncementOfferPayload>(env);
            if (payload is null) return false;
            var ann = Read(payload.AnnouncementId);
            if (ann is null) return false;
            if (payload.OfferExpiresAt <= NowMs)
            {
                RecordDelivery(payload.AnnouncementId, deviceId, ReceiptState.Expired, "offer_expired", "server");
                return false;
            }
            if (ann.Kind == "live" && ann.Status == "ended") return false;    // live audio is never replayed
            return true;
        }
        if (env.Type == Protocol.TMediaToken)
        {
            var payload = PayloadSerializer.ToPayload<MediaTokenPayload>(env);
            if (payload is null) return false;
            using var conn = _db.Open();
            var ok = conn.Exists("""
                SELECT COUNT(*) FROM media_tokens
                WHERE announcement_id = $a AND device_id = $d AND used = 0 AND expires_at > $n
                """, ("$a", payload.AnnouncementId), ("$d", deviceId), ("$n", NowMs));
            return ok;
        }
        return true;
    }

    // ---------------------------------------------------------------- end of announcement

    /// Send announcement.end, close the office mic socket and every device stream, finalize the
    /// recording, and queue transcription. Idempotent.
    public bool End(string announcementId, string endedBy, string actor)
    {
        var ann = Read(announcementId);
        if (ann is null) return false;
        long now = NowMs;

        List<(string DeviceId, string Label)> targets;
        using (var conn = _db.Open())
        {
            targets = SnapshotDevices(conn, announcementId);
            int changed = conn.Run("""
                UPDATE announcements SET status = 'ended', ended_at = $t, ended_by = $b, window_closes_at = NULL,
                       frames_sent = $f
                WHERE announcement_id = $a AND status = 'active'
                """,
                ("$t", now), ("$b", endedBy), ("$f", SumFramesSent(announcementId)), ("$a", announcementId));
            if (changed == 0) return false;
            EnsureTranscriptQueued(conn, null, announcementId, now);
            Audit.Add(conn, null, actor, "announcement.end",
                $"announcement={announcementId} ended_by={endedBy} devices={targets.Count}");
        }

        var endEnv = ControlEnvelope.Create(Protocol.TAnnouncementEnd,
            PayloadSerializer.Node(new AnnouncementEndPayload(announcementId, endedBy, now)));
        foreach (var (deviceId, _) in targets) _outbox.Enqueue(deviceId, endEnv);
        _media.EndAnnouncement(announcementId);
        _media.Forget(announcementId);

        _log.LogInformation("announcement {AnnouncementId} ended ({EndedBy}) for {Count} device(s); recording {Path}",
            announcementId, endedBy, targets.Count, ann.RecordingPath ?? "none");
        return true;
    }

    private long SumFramesSent(string announcementId) => _media.StreamFramesTotal(announcementId);

    /// Finalize the recording of a live session and store its honest metadata.
    public void StoreRecording(string announcementId, WavFinal final, string path)
    {
        using var conn = _db.Open();
        conn.Run("""
            UPDATE announcements SET recording_path = $p, recording_sha256 = $s, recording_bytes = $b,
                   recording_duration_ms = $d, recording_sample_rate = 16000
            WHERE announcement_id = $a
            """,
            ("$p", path), ("$s", final.Sha256), ("$b", final.Bytes), ("$d", final.DurationMs), ("$a", announcementId));
        _log.LogInformation("announcement {AnnouncementId} recording finalized: {Samples} samples, {Bytes} bytes, sha256 {Sha}",
            announcementId, final.Samples, final.Bytes, final.Sha256[..12]);
    }

    // ---------------------------------------------------------------- helpers

    private static ControlEnvelope OfferEnvelope(string announcementId, string kind, string snapshotId,
        string priority, long offerExpiresAt, int maxDurationS, bool chime, string sourceLabel) =>
        ControlEnvelope.Create(Protocol.TAnnouncementOffer, PayloadSerializer.Node(new AnnouncementOfferPayload(
            announcementId, kind, snapshotId, priority,
            new MediaFormat("pcm_s16le", Protocol.SampleRate, 1, Protocol.FrameMs),
            offerExpiresAt, maxDurationS, chime, sourceLabel)));

    public AnnouncementRow? Read(string announcementId)
    {
        using var conn = _db.Open();
        return Read(conn, announcementId);
    }

    private static readonly string AnnColumns = """
        announcement_id, kind, status, created_by, created_at, started_at, window_closes_at, ended_at, ended_by,
        max_duration_s, offer_expires_at, chime, target_json, target_label, audience_snapshot_id, lease_id,
        source_label, recording_path, recording_sha256, recording_bytes, recording_duration_ms,
        recording_sample_rate, frames_sent
        """;

    private static AnnouncementRow Read(SqliteConnection conn, string announcementId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {AnnColumns} FROM announcements WHERE announcement_id = $a";
        cmd.Parameters.AddWithValue("$a", announcementId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AnnouncementRow(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            NullLong(r, 5), NullLong(r, 6), NullLong(r, 7), NullString(r, 8), r.GetInt32(9), r.GetInt64(10),
            r.GetInt32(11) == 1, r.GetString(12), r.GetString(13), r.GetString(14), NullString(r, 15),
            r.GetString(16), NullString(r, 17), NullString(r, 18), NullLong(r, 19), NullLong(r, 20),
            NullInt(r, 21), r.GetInt64(22));
    }

    private static string? NullString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? NullLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static int? NullInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public List<(string DeviceId, string Label)> SnapshotDevices(string announcementId)
    {
        using var conn = _db.Open();
        return SnapshotDevices(conn, announcementId);
    }

    private static List<(string, string)> SnapshotDevices(SqliteConnection conn, string announcementId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id, label FROM announcement_devices WHERE announcement_id = $a ORDER BY device_id";
        cmd.Parameters.AddWithValue("$a", announcementId);
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    private bool IsTargeted(string announcementId, string deviceId)
    {
        using var conn = _db.Open();
        return conn.Exists("SELECT COUNT(*) FROM announcement_devices WHERE announcement_id = $a AND device_id = $d",
            ("$a", announcementId), ("$d", deviceId));
    }

    public void RecordDelivery(string announcementId, string deviceId, string state, string? errorCode, string source)
    {
        using var conn = _db.Open();
        RecordDelivery(conn, null, announcementId, deviceId, state, errorCode, source);
    }

    private static void RecordDelivery(SqliteConnection conn, SqliteTransaction? tx, string announcementId,
        string deviceId, string state, string? errorCode, string source)
    {
        conn.Run(tx, """
            INSERT INTO delivery_results(announcement_id, device_id, state, error_code, source, updated_at)
            VALUES ($a, $d, $s, $e, $o, $t)
            ON CONFLICT(announcement_id, device_id, state) DO UPDATE SET error_code = $e, source = $o, updated_at = $t
            """,
            ("$a", announcementId), ("$d", deviceId), ("$s", state), ("$e", errorCode), ("$o", source),
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// Resolve the target SERVER-SIDE to the exact device list frozen for this announcement.
    private (List<(string DeviceId, string Label)> Devices, string Label) ResolveTarget(
        SqliteConnection conn, TargetScope target)
    {
        var list = new List<(string, string)>();
        string scopeLabel;

        if (target.All == true)
        {
            scopeLabel = "all endpoints";
            QueryDevices(conn, "SELECT device_id, name, computer_name, active_user FROM devices WHERE revoked = 0 ORDER BY name", list);
            return (list, scopeLabel);
        }
        if (target.Groups is { Count: > 0 })
        {
            var names = new List<string>();
            foreach (var g in target.Groups)
            {
                var name = conn.Scalar("SELECT name FROM groups WHERE id = $g", ("$g", g)) as string;
                names.Add(name ?? $"group {g}");
            }
            scopeLabel = "groups: " + string.Join(", ", names);
            var ps = target.Groups.Select((g, i) => ($"$g{i}", (object)g)).ToArray();
            var inClause = string.Join(",", ps.Select(p => p.Item1));
            QueryDevices(conn, $"""
                SELECT DISTINCT d.device_id, d.name, d.computer_name, d.active_user FROM devices d
                JOIN group_members gm ON gm.device_id = d.device_id
                WHERE d.revoked = 0 AND gm.group_id IN ({inClause}) ORDER BY d.name
                """, list, ps);
            return (list, scopeLabel);
        }
        if (target.Devices is { Count: > 0 })
        {
            var ps = target.Devices.Select((d, i) => ($"$d{i}", (object)d)).ToArray();
            var inClause = string.Join(",", ps.Select(p => p.Item1));
            QueryDevices(conn, $"""
                SELECT device_id, name, computer_name, active_user FROM devices
                WHERE revoked = 0 AND device_id IN ({inClause}) ORDER BY name
                """, list, ps);
            scopeLabel = target.Devices.Count == 1 ? "1 endpoint" : $"{target.Devices.Count} endpoints";
            return (list, scopeLabel);
        }
        return (list, "no audience selected");
    }

    private static void QueryDevices(SqliteConnection conn, string sql, List<(string, string)> into,
        (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(1);
            var computer = r.IsDBNull(2) ? null : r.GetString(2);
            var user = r.IsDBNull(3) ? null : r.GetString(3);
            into.Add((r.GetString(0), user is not null ? $"{user} ({computer ?? name})" : $"No signed-in user ({computer ?? name})"));
        }
    }

    private static void QueryDevices(SqliteConnection conn, string sql, List<(string, string)> into) =>
        QueryDevices(conn, sql, into, []);
}

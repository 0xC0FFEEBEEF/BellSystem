using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record DeviceDeliveryView(string DeviceId, string Label, string State, string? ErrorCode,
    string Source, long At, List<string> AllStates);

/// Honest per-announcement delivery report: one FINAL state per device plus the full state trail.
/// `completed` only ever comes from the device's own receipt (plan §12: a socket delivery is never
/// an audible delivery), so `audible_completed` and `socket_delivered` are reported separately.
public sealed record AnnouncementView(AnnouncementRow Announcement, List<DeviceDeliveryView> Devices,
    Dictionary<string, int> FinalCounts, int Targeted, int AudibleCompleted, int SocketDelivered,
    Dictionary<string, int> TranscriptStatus)
{
    public object? RecordingView() => Announcement.RecordingPath is null ? null : new
    {
        path = Announcement.RecordingPath,
        sha256 = Announcement.RecordingSha256,
        bytes = Announcement.RecordingBytes,
        duration_ms = Announcement.RecordingDurationMs,
        sample_rate = Announcement.RecordingSampleRate,
        audio_url = $"api/announcements/{Announcement.AnnouncementId}/audio",
    };
}

public sealed record TranscriptRow(string AnnouncementId, string? Original, string? Current, string Status,
    string? Engine, string? CorrectedBy, long? CorrectedAt, bool Purged, long CreatedAt);

public sealed record TranscriptOutcome(int Status, string? Detail);

public sealed record RetentionReport(int AudioDeleted, int TranscriptsPurged, int AudioRetentionDays,
    int TranscriptRetentionDays);

public sealed partial class AnnouncementsService
{
    // ---------------------------------------------------------------- history

    public List<AnnouncementView> History(CurrentUser user, string? kind, long? from, long? to,
        string? deviceId, int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);
        var ids = new List<string>();
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT announcement_id FROM announcements a
                WHERE ($k IS NULL OR kind = $k)
                  AND ($f IS NULL OR created_at >= $f)
                  AND ($t IS NULL OR created_at <= $t)
                  AND ($dv IS NULL OR EXISTS (SELECT 1 FROM announcement_devices d
                                             WHERE d.announcement_id = a.announcement_id AND d.device_id = $dv))
                ORDER BY created_at DESC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$k", string.IsNullOrEmpty(kind) ? (object)DBNull.Value : kind);
            cmd.Parameters.AddWithValue("$f", from is null ? (object)DBNull.Value : from.Value);
            cmd.Parameters.AddWithValue("$t", to is null ? (object)DBNull.Value : to.Value);
            cmd.Parameters.AddWithValue("$dv", string.IsNullOrEmpty(deviceId) ? (object)DBNull.Value : deviceId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }

        var views = new List<AnnouncementView>();
        foreach (var id in ids)
        {
            var view = View(id);
            if (view is null) continue;
            // Operators only see announcements they could themselves have addressed (plan §11).
            if (user.Role == "operator" && _leases.GateTarget(user, view.Announcement.Target).Status != 200) continue;
            views.Add(view);
        }
        return views;
    }

    public AnnouncementView? View(string announcementId)
    {
        var ann = Read(announcementId);
        if (ann is null) return null;

        var devices = new List<DeviceDeliveryView>();
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT d.device_id, d.label, r.state, r.error_code, r.source, r.updated_at
                FROM announcement_devices d
                LEFT JOIN delivery_results r ON r.announcement_id = d.announcement_id AND r.device_id = d.device_id
                WHERE d.announcement_id = $a
                ORDER BY d.label, r.updated_at
                """;
            cmd.Parameters.AddWithValue("$a", announcementId);
            var perDevice = new Dictionary<string, List<(string State, string? Code, string Source, long At)>>();
            var labels = new Dictionary<string, string>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var devId = r.GetString(0);
                    labels[devId] = r.GetString(1);
                    var state = r.IsDBNull(2) ? "unknown" : r.GetString(2);
                    var code = r.IsDBNull(3) ? null : r.GetString(3);
                    var source = r.IsDBNull(4) ? "server" : r.GetString(4);
                    var at = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                    if (!perDevice.TryGetValue(devId, out var list))
                    {
                        list = [];
                        perDevice[devId] = list;
                    }
                    list.Add((state, code, source, at));
                }
            }

            foreach (var (devId, rows) in perDevice)
            {
                // Final state: a completed receipt always wins; otherwise the most recent state.
                var completed = rows.FirstOrDefault(x => x.State == ReceiptState.Completed);
                var final = completed.State == ReceiptState.Completed
                    ? completed
                    : rows.OrderByDescending(x => x.At).First();
                devices.Add(new DeviceDeliveryView(devId, labels[devId], final.State, final.Code, final.Source,
                    final.At, rows.Select(x => x.State).Distinct().ToList()));
            }
        }

        var counts = new Dictionary<string, int>();
        foreach (var d in devices) counts[d.State] = counts.TryGetValue(d.State, out var c) ? c + 1 : 1;
        int audible = counts.TryGetValue(ReceiptState.Completed, out var a) ? a : 0;
        int delivered = devices.Count(d => d.AllStates.Contains(ReceiptState.Started));

        var transcriptStatus = new Dictionary<string, int>();
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, COUNT(*) FROM transcripts GROUP BY status";
            using var r = cmd.ExecuteReader();
            while (r.Read()) transcriptStatus[r.GetString(0)] = r.GetInt32(1);
        }

        return new AnnouncementView(ann, devices, counts, devices.Count, audible, delivered, transcriptStatus);
    }

    public TranscriptRow? Transcript(string announcementId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT transcript_original, transcript_current, status, engine, corrected_by, corrected_at, purged, created_at
            FROM transcripts WHERE announcement_id = $a
            """;
        cmd.Parameters.AddWithValue("$a", announcementId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new TranscriptRow(announcementId, NullString(r, 0), NullString(r, 1), r.GetString(2),
            NullString(r, 3), NullString(r, 4), NullLong(r, 5), r.GetInt32(6) == 1, r.GetInt64(7));
    }

    /// Office correction: transcript_original is never overwritten, and every correction is an
    /// audit row (plan §11). Role-gated: viewers cannot correct.
    public TranscriptOutcome CorrectTranscript(CurrentUser user, string announcementId, string text)
    {
        if (user.Role is not ("admin" or "operator"))
            return new(403, "only administrators and operators can correct transcripts");
        if (string.IsNullOrWhiteSpace(text)) return new(400, "text is required");
        if (text.Length > 200_000) return new(400, "transcript text is too long");
        var ann = Read(announcementId);
        if (ann is null) return new(404, "no such announcement");
        if (user.Role == "operator" && _leases.GateTarget(user, ann.Target).Status != 200)
            return new(403, "this announcement is outside your group scope");

        long now = NowMs;
        using var conn = _db.Open();
        int changed = conn.Run("""
            INSERT INTO transcripts(announcement_id, transcript_original, transcript_current, status, corrected_by,
                                    corrected_at, created_at, updated_at)
            VALUES ($a, NULL, $x, 'completed', $u, $t, $t, $t)
            ON CONFLICT(announcement_id) DO UPDATE SET transcript_current = $x, corrected_by = $u,
                   corrected_at = $t, updated_at = $t, status = 'completed'
            """,
            ("$a", announcementId), ("$x", text), ("$u", user.Username), ("$t", now));
        Audit.Add(conn, null, user.Username, "transcript.correct",
            $"announcement={announcementId} chars={text.Length} original_preserved=1");
        _log.LogInformation("transcript for {AnnouncementId} corrected by {User} (original version preserved)",
            announcementId, user.Username);
        conn.Dispose();
        PushHistory(announcementId);
        return changed > 0 ? new(200, null) : new(500, "correction was not stored");
    }

    // ---------------------------------------------------------------- transcription queue

    private static void EnsureTranscriptQueued(SqliteConnection conn, SqliteTransaction? tx, string announcementId, long now)
    {
        const string transcriptSql = """
            INSERT INTO transcripts(announcement_id, status, created_at, updated_at)
            VALUES ($a, 'pending', $t, $t) ON CONFLICT(announcement_id) DO NOTHING
            """;
        const string jobSql = """
            INSERT INTO transcript_jobs(announcement_id, status, attempts, max_attempts, created_at, updated_at)
            SELECT $a, 'queued', 0, $m, $t, $t
            WHERE NOT EXISTS (SELECT 1 FROM transcript_jobs WHERE announcement_id = $a)
            """;
        var tps = new (string, object?)[] { ("$a", announcementId), ("$t", now) };
        var jps = new (string, object?)[] { ("$a", announcementId),
            ("$m", LiveSettings.TranscriptionMaxAttempts), ("$t", now) };
        if (tx is null) { conn.Run(transcriptSql, tps); conn.Run(jobSql, jps); }
        else { conn.Run(tx, transcriptSql, tps); conn.Run(tx, jobSql, jps); }
    }

    public object TranscriptQueueStatus()
    {
        using var conn = _db.Open();
        var counts = new Dictionary<string, int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT status, COUNT(*) FROM transcript_jobs GROUP BY status";
            using var r = cmd.ExecuteReader();
            while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
        }
        var tcounts = new Dictionary<string, int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT status, COUNT(*) FROM transcripts GROUP BY status";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tcounts[r.GetString(0)] = r.GetInt32(1);
        }
        return new
        {
            transcription_enabled = _settings.TranscriptionEnabled,
            engine = "whisper.cpp / small.en (local server)",
            note = _settings.TranscriptionEnabled
                ? "Local transcription is on. Playback never waits for it; failed work is retried by the durable queue."
                : "Transcription was disabled by the administrator; jobs stay queued and no transcript is faked.",
            jobs = new
            {
                queued = counts.GetValueOrDefault("queued"),
                running = counts.GetValueOrDefault("running"),
                completed = counts.GetValueOrDefault("completed"),
                failed = counts.GetValueOrDefault("failed"),
            },
            max_attempts = LiveSettings.TranscriptionMaxAttempts,
            transcripts = new
            {
                pending = tcounts.GetValueOrDefault("pending"),
                completed = tcounts.GetValueOrDefault("completed"),
                failed = tcounts.GetValueOrDefault("failed"),
            },
        };
    }

    // ---------------------------------------------------------------- active announcements

    public object ActiveSummary(CurrentUser user)
    {
        var lease = _leases.ActiveLease();
        var list = new List<string>();
        using var conn = _db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT announcement_id FROM announcements
                WHERE status = 'active' ORDER BY created_at DESC LIMIT 50
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }

        var announcements = new List<object>();
        foreach (var id in list)
        {
            var view = View(id);
            if (view is null) continue;
            if (user.Role == "operator" && _leases.GateTarget(user, view.Announcement.Target).Status != 200) continue;
            var a = view.Announcement;
            announcements.Add(new
            {
                announcement_id = a.AnnouncementId,
                kind = a.Kind,
                created_by = a.CreatedBy,
                target_label = a.TargetLabel,
                audience_snapshot_id = a.AudienceSnapshotId,
                device_count = view.Targeted,
                counts = view.FinalCounts,
                audible_completed = view.AudibleCompleted,
                socket_delivered = view.SocketDelivered,
                created_at = a.CreatedAt,
                started_at = a.StartedAt,
                window_closes_at = a.WindowClosesAt,
                offer_expires_at = a.OfferExpiresAt,
                max_duration_s = a.MaxDurationS,
                elapsed_s = (NowMs - a.CreatedAt) / 1000,
                lease_id = a.LeaseId,
            });
        }

        return new
        {
            lease = lease is null ? null : new
            {
                lease_id = lease.LeaseId,
                holder = lease.Username,
                target_label = lease.Target.All == true ? "all endpoints" : "scoped audience",
                created_at = lease.CreatedAt,
                expires_at = lease.ExpiresAt,
                elapsed_s = (NowMs - lease.CreatedAt) / 1000,
                max_duration_s = lease.MaxDurationS,
                renew_interval_s = lease.RenewIntervalS,
                renew_deadline = lease.RenewDeadline,
                announcement_id = lease.AnnouncementId,
            },
            announcements,
        };
    }

    // ---------------------------------------------------------------- retention

    /// Delete expired recordings and purge expired transcripts (plan §11 suggested pilot defaults:
    /// 7 days audio, 30 days transcripts/audit). Audit rows are never deleted; counts are logged.
    public RetentionReport RunRetention(string actor)
    {
        long now = NowMs;
        long audioCutoff = now - _settings.AudioRetentionDays * 86_400_000L;
        long transcriptCutoff = now - _settings.TranscriptRetentionDays * 86_400_000L;
        int audio = 0, transcripts = 0;

        var paths = new List<(string Id, string Path)>();
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT announcement_id, recording_path FROM announcements
                WHERE recording_path IS NOT NULL AND COALESCE(ended_at, created_at) < $c
                """;
            cmd.Parameters.AddWithValue("$c", audioCutoff);
            using var r = cmd.ExecuteReader();
            while (r.Read()) paths.Add((r.GetString(0), r.GetString(1)));
        }
        foreach (var (id, path) in paths)
        {
            try
            {
                if (path.StartsWith(_cfg.DataDir, StringComparison.Ordinal) && File.Exists(path)) File.Delete(path);
            }
            catch (IOException e) { _log.LogWarning("retention could not delete {Path}: {Error}", path, e.Message); }
            using var conn = _db.Open();
            conn.Run("""
                UPDATE announcements SET recording_path = NULL, recording_sha256 = NULL, recording_bytes = NULL,
                       recording_duration_ms = NULL, recording_sample_rate = NULL
                WHERE announcement_id = $a
                """, ("$a", id));
            audio++;
        }

        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT announcement_id FROM transcripts WHERE purged = 0 AND created_at < $c
                """;
            cmd.Parameters.AddWithValue("$c", transcriptCutoff);
            var ids = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) ids.Add(r.GetString(0));
            foreach (var id in ids)
            {
                conn.Run("""
                    UPDATE transcripts SET transcript_original = NULL, transcript_current = NULL, purged = 1,
                           status = 'failed', engine = 'retention_purged', updated_at = $t
                    WHERE announcement_id = $a
                    """, ("$t", now), ("$a", id));
                conn.Run("DELETE FROM transcript_jobs WHERE announcement_id = $a", ("$a", id));
                transcripts++;
            }
            if (audio > 0 || transcripts > 0)
                Audit.Add(conn, null, actor, "retention.run",
                    $"audio_deleted={audio} transcripts_purged={transcripts} " +
                    $"audio_retention_days={_settings.AudioRetentionDays} transcript_retention_days={_settings.TranscriptRetentionDays}");
        }

        if (audio > 0 || transcripts > 0)
            _log.LogInformation("retention ran: {Audio} recording(s) deleted, {Transcripts} transcript(s) purged (audit kept)",
                audio, transcripts);
        return new RetentionReport(audio, transcripts, _settings.AudioRetentionDays, _settings.TranscriptRetentionDays);
    }

    // ---------------------------------------------------------------- diagnostics helpers

    // ---- diagnostics accessors (used by /api/diagnostics) ----

    public List<QueueDepth> MediaQueueDepths() => _media.QueueDepths();
    public int ActiveMicConnections() => _media.ActiveMicConnections;

    public int ActiveAnnouncementCount()
    {
        using var conn = _db.Open();
        return conn.ScalarInt("SELECT COUNT(*) FROM announcements WHERE status = 'active'");
    }

    /// Live announcements in progress (recorded ones stay "active" until their offer expires,
    /// which can be a day, so they must not block maintenance such as server updates).
    public int ActiveLiveCount()
    {
        using var conn = _db.Open();
        return conn.ScalarInt("SELECT COUNT(*) FROM announcements WHERE status = 'active' AND kind = 'live'");
    }
}

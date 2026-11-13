using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

/// Hosted sweep loop (500 ms): close preparation windows, expire overdue talk leases (ending the
/// live announcements they held with announcement.end(ended_by: lease)), and run the max-duration
/// watchdog (PROTOCOL.md §7) plus recorded-offer expiry.
public sealed class AnnouncementSweeperHost : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly TalkLeaseService _leases;
    private readonly AnnouncementsService _announcements;
    private readonly ILogger<AnnouncementSweeperHost> _log;

    public AnnouncementSweeperHost(TalkLeaseService leases, AnnouncementsService announcements,
        ILogger<AnnouncementSweeperHost> log)
    {
        _leases = leases; _announcements = announcements; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                _announcements.ClosePreparationWindows();

                // Max-duration watchdog first: an announcement that reaches start + max_duration_s is
                // attributed to its own limit (ended_by: timeout). A lease that lapses without renewal
                // ends the announcement as ended_by: lease.
                _announcements.WatchdogSweep();

                foreach (var lease in _leases.Sweep())
                    if (lease.AnnouncementId is not null)
                    {
                        string endedBy = lease.EndedReason == "max_duration" ? "timeout" : "lease";
                        _log.LogInformation("lease {LeaseId} expired ({Reason}) -> announcement.end(ended_by: {EndedBy}) for {AnnouncementId}",
                            lease.LeaseId, lease.EndedReason, endedBy, lease.AnnouncementId);
                        _announcements.End(lease.AnnouncementId, endedBy, "sweeper");
                    }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "announcement sweep failed");
            }
        }
    }
}

/// A finalized recording immediately gets a durable transcript_jobs row (max 5 attempts) and a
/// pending transcript, independently of the announcement delivery window.
/// No transcript text is ever invented here. The worker only runs when transcription is enabled
/// and the bundled loopback whisper.cpp service is available; otherwise jobs remain queued.
public interface ITranscriptionEngine
{
    string Name { get; }
    bool Available { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);
    Task<string?> TranscribeAsync(ReadOnlyMemory<byte> wav, CancellationToken ct);
    Task<string?> TranscribePreviewAsync(ReadOnlyMemory<byte> wav, CancellationToken ct) => TranscribeAsync(wav, ct);
}

/// Test/fallback engine. Available=false keeps every job queued.
public sealed class NoTranscriptionEngine : ITranscriptionEngine
{
    public string Name => "none";
    public bool Available => false;
    public Task<string?> TranscribeAsync(ReadOnlyMemory<byte> wav, CancellationToken ct) => Task.FromResult<string?>(null);
}

public sealed class TranscriptionWorkerHost : BackgroundService
{
    private readonly Db _db;
    private readonly LiveSettings _settings;
    private readonly AnnouncementsService _announcements;
    private readonly ITranscriptionEngine _engine;
    private readonly TimeProvider _time;
    private readonly ILogger<TranscriptionWorkerHost> _log;

    public TranscriptionWorkerHost(Db db, LiveSettings settings, AnnouncementsService announcements,
        ITranscriptionEngine engine, TimeProvider time, ILogger<TranscriptionWorkerHost> log)
    {
        _db = db; _settings = settings; _announcements = announcements; _engine = engine; _time = time; _log = log;
    }

    public void RecoverInterruptedJobs()
    {
        using var conn = _db.Open();
        var count = conn.Run("""
            UPDATE transcript_jobs SET status = 'queued', attempts = MAX(0, attempts - 1),
                last_error = 'interrupted by server restart; queued again', next_attempt_at = 0, updated_at = $t
            WHERE status = 'running'
            """, ("$t", _time.GetUtcNow().ToUnixTimeMilliseconds()));
        if (count > 0) _log.LogWarning("recovered {Count} interrupted transcription job(s)", count);
        _announcements.RepairTranscriptionQueue();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        RecoverInterruptedJobs();
        long nextRepair = _time.GetUtcNow().ToUnixTimeMilliseconds() + 60_000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_time.GetUtcNow().ToUnixTimeMilliseconds() >= nextRepair)
                {
                    _announcements.RepairTranscriptionQueue();
                    nextRepair = _time.GetUtcNow().ToUnixTimeMilliseconds() + 60_000;
                }
                if (await RunOneAsync(ct)) continue; // drain eligible work without a five-second gap
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogError(e, "transcription worker pass failed; work remains queued");
            }
            await _announcements.WaitForTranscriptionWorkAsync(ct);
        }
    }

    public async Task<bool> RunOneAsync(CancellationToken ct)
    {
        if (!_settings.TranscriptionEnabled) return false;
        using (var conn = _db.Open())
            if (!conn.Exists("SELECT COUNT(*) FROM transcript_jobs WHERE status = 'queued' AND next_attempt_at <= $t",
                ("$t", _time.GetUtcNow().ToUnixTimeMilliseconds()))) return false;
        if (!await _engine.IsAvailableAsync(ct)) return false;
        var job = ClaimJob();
        if (job is null) return false;
        var (jobId, annId, path, attempt) = job.Value;
        var started = System.Diagnostics.Stopwatch.StartNew();
        string? text = null;
        string? error = null;
        try
        {
            if (path is null) throw new InvalidOperationException("the announcement has no finalized recording");
            var wav = await File.ReadAllBytesAsync(path, ct);
            text = await _engine.TranscribeAsync(wav, ct);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Whisper returned no text for the recording; retrying");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using var conn = _db.Open();
            conn.Run("UPDATE transcript_jobs SET status = 'queued', attempts = MAX(0, attempts - 1), next_attempt_at = 0 WHERE job_id = $j AND status = 'running'", ("$j", jobId));
            throw;
        }
        catch (Exception e) { text = null; error = e is OperationCanceledException ? "Whisper request timed out; retrying" : e.Message; }
        StoreResult(jobId, annId, text, error, attempt, started.ElapsedMilliseconds);
        return true;
    }

    private (long JobId, string AnnouncementId, string? Path, int Attempt)? ClaimJob()
    {
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE transcript_jobs SET status = 'running', attempts = attempts + 1, updated_at = $t
            WHERE job_id = (SELECT job_id FROM transcript_jobs WHERE status = 'queued' AND attempts < max_attempts AND next_attempt_at <= $t
                            ORDER BY created_at DESC, job_id DESC LIMIT 1)
            RETURNING job_id, announcement_id, attempts
            """;
        cmd.Parameters.AddWithValue("$t", now);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        long jobId = r.GetInt64(0);
        string annId = r.GetString(1);
        int attempt = r.GetInt32(2);
        r.Close();
        var path = conn.Scalar("SELECT recording_path FROM announcements WHERE announcement_id = $a AND recording_sha256 IS NOT NULL", ("$a", annId)) as string;
        _log.LogInformation("transcription job {JobId} claimed for announcement {AnnouncementId} (attempt {Attempt})", jobId, annId, attempt);
        return (jobId, annId, path, attempt);
    }

    private void StoreResult(long jobId, string announcementId, string? text, string? error, int attempts, long elapsedMs)
    {
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = _db.Open();
        int max = conn.ScalarInt("SELECT max_attempts FROM transcript_jobs WHERE job_id = $j", ("$j", jobId));
        bool success = error is null && !string.IsNullOrWhiteSpace(text);
        bool exhausted = attempts >= max;
        using (var tx = conn.BeginTransaction())
        {
            var n = conn.Run(tx, """
                UPDATE transcript_jobs SET status = $s, last_error = $e, updated_at = $t, next_attempt_at = $next
                WHERE job_id = $j AND status = 'running'
                """, ("$s", success ? "completed" : exhausted ? "failed" : "queued"), ("$e", error), ("$t", now),
                ("$next", success ? 0 : now + Math.Min(60_000, 1000L << Math.Min(attempts - 1, 6))), ("$j", jobId));
            if (n == 0) return; // removed by retention/redo: do not overwrite a newer transcript
            if (success)
                conn.Run(tx, """
                    UPDATE transcripts SET transcript_original = $x, transcript_current = $x, status = 'completed',
                        engine = $e, corrected_by = NULL, corrected_at = NULL, purged = 0, updated_at = $t
                    WHERE announcement_id = $a
                    """, ("$x", text!.Trim()), ("$e", _engine.Name), ("$t", now), ("$a", announcementId));
            else
                conn.Run(tx, "UPDATE transcripts SET status = $s, updated_at = $t WHERE announcement_id = $a",
                    ("$s", exhausted ? "failed" : "pending"), ("$t", now), ("$a", announcementId));
            tx.Commit();
        }
        Db.SetSetting(conn, "transcription_last_duration_ms", elapsedMs.ToString());
        if (success) _log.LogInformation("transcript stored for {AnnouncementId}: {Chars} chars in {ElapsedMs} ms", announcementId, text!.Length, elapsedMs);
        else _log.LogWarning("transcription job {JobId} for {AnnouncementId} failed after {ElapsedMs} ms (attempt {Attempts}/{Max}): {Error}", jobId, announcementId, elapsedMs, attempts, max, error);
        conn.Dispose();
        _announcements.PushHistory(announcementId);
    }
}

/// Retention worker (plan §11): deletes expired recordings and purges expired transcripts on a
/// schedule; counts are logged and an audit row is kept. Audit rows are never deleted.
public sealed class RetentionWorkerHost : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly AnnouncementsService _announcements;
    private readonly ILogger<RetentionWorkerHost> _log;
    private readonly ClientLogsService _clientLogs;

    public RetentionWorkerHost(AnnouncementsService announcements, ClientLogsService clientLogs, ILogger<RetentionWorkerHost> log)
    {
        _announcements = announcements; _clientLogs = clientLogs; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { _announcements.RunRetention("retention-worker"); _clientLogs.Retain(); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "retention pass failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}

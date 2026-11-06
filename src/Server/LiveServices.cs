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

/// The transcription pipeline is real up to the engine boundary: on announcement completion a
/// durable transcript_jobs row is queued (max 5 attempts) and a transcript row starts as `pending`.
/// No transcript text is ever invented here. The worker only runs when transcription is enabled
/// and the bundled loopback whisper.cpp service is available; otherwise jobs remain queued.
public interface ITranscriptionEngine
{
    string Name { get; }
    bool Available { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);
    Task<string?> TranscribeAsync(ReadOnlyMemory<byte> wav, CancellationToken ct);
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
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? lastIdleReason = null;
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                // Re-read every pass: enabling transcription in /admin or starting the whisper
                // service takes effect without restarting SchoolBell. Playback never waits on this.
                string? idle = !_settings.TranscriptionEnabled ? "settings.transcription_enabled=false"
                    : !await _engine.IsAvailableAsync(ct) ? $"engine '{_engine.Name}' unavailable" : null;
                if (idle is not null)
                {
                    if (idle != lastIdleReason)
                        _log.LogInformation("transcription worker idle ({Reason}); {Queued} job(s) queued — no transcript is faked", idle, Queued());
                    lastIdleReason = idle;
                    continue;
                }
                if (lastIdleReason is not null) _log.LogInformation("transcription worker running with engine {Engine}", _engine.Name);
                lastIdleReason = null;
                var job = ClaimJob();
                if (job is null) continue;
                var (jobId, annId, path) = job.Value;
                string? text = null;
                string? error = null;
                try
                {
                    if (path is null) throw new InvalidOperationException("the announcement has no stored recording");
                    var wav = await File.ReadAllBytesAsync(path, ct);
                    text = await _engine.TranscribeAsync(wav, ct);
                    if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("the engine returned no transcript text");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { error = e.Message; }

                StoreResult(jobId, annId, text, error);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "transcription worker pass failed");
            }
        }
    }

    private int Queued()
    {
        using var conn = _db.Open();
        return conn.ScalarInt("SELECT COUNT(*) FROM transcript_jobs WHERE status = 'queued'");
    }

    /// Atomic claim of the oldest eligible job, incrementing the attempt counter.
    private (long JobId, string AnnouncementId, string? Path)? ClaimJob()
    {
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE transcript_jobs SET status = 'running', attempts = attempts + 1, updated_at = $t
            WHERE job_id = (SELECT job_id FROM transcript_jobs WHERE status = 'queued' AND attempts < max_attempts
                            ORDER BY job_id LIMIT 1)
            RETURNING job_id, announcement_id
            """;
        cmd.Parameters.AddWithValue("$t", now);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        long jobId = r.GetInt64(0);
        string annId = r.GetString(1);
        r.Close();
        var path = conn.Scalar("SELECT recording_path FROM announcements WHERE announcement_id = $a", ("$a", annId)) as string;
        _log.LogInformation("transcription job {JobId} claimed for announcement {AnnouncementId} (attempt {Attempt})", jobId, annId, 1);
        return (jobId, annId, path);
    }

    private void StoreResult(long jobId, string announcementId, string? text, string? error)
    {
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = _db.Open();
        if (text is not null)
        {
            conn.Run("""
                UPDATE transcripts SET transcript_original = $x, transcript_current = $x, status = 'completed',
                       engine = $e, updated_at = $t
                WHERE announcement_id = $a
                """, ("$x", text), ("$e", _engine.Name), ("$t", now), ("$a", announcementId));
            conn.Run("UPDATE transcript_jobs SET status = 'completed', last_error = NULL, updated_at = $t WHERE job_id = $j",
                ("$t", now), ("$j", jobId));
            _log.LogInformation("transcript stored for announcement {AnnouncementId} ({Chars} chars)", announcementId, text.Length);
            conn.Dispose();
            _announcements.PushHistory(announcementId);
            return;
        }

        int attempts = Convert.ToInt32(conn.Scalar("SELECT attempts FROM transcript_jobs WHERE job_id = $j", ("$j", jobId)) ?? 0);
        int maxAttempts = Convert.ToInt32(conn.Scalar("SELECT max_attempts FROM transcript_jobs WHERE job_id = $j", ("$j", jobId)) ?? 5);
        bool exhausted = attempts >= maxAttempts;
        conn.Run("UPDATE transcript_jobs SET status = $s, last_error = $e, updated_at = $t WHERE job_id = $j",
            ("$s", exhausted ? "failed" : "queued"), ("$e", error), ("$t", now), ("$j", jobId));
        if (exhausted)
        {
            conn.Run("UPDATE transcripts SET status = 'failed', updated_at = $t WHERE announcement_id = $a", ("$t", now), ("$a", announcementId));
            _announcements.PushHistory(announcementId);
        }
        _log.LogWarning("transcription job {JobId} for announcement {AnnouncementId} failed (attempt {Attempts}/{Max}): {Error}",
            jobId, announcementId, attempts, maxAttempts, error);
    }
}

/// Retention worker (plan §11): deletes expired recordings and purges expired transcripts on a
/// schedule; counts are logged and an audit row is kept. Audit rows are never deleted.
public sealed class RetentionWorkerHost : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly AnnouncementsService _announcements;
    private readonly ILogger<RetentionWorkerHost> _log;

    public RetentionWorkerHost(AnnouncementsService announcements, ILogger<RetentionWorkerHost> log)
    {
        _announcements = announcements; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { _announcements.RunRetention("retention-worker"); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "retention pass failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}

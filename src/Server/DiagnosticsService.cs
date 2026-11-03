namespace SchoolBell.Server;

/// Diagnostics with every queue depth and limit visible (plan §14: configurable + visible,
/// never a hidden cap).
public sealed class DiagnosticsService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly TimeProvider _time;
    private readonly ControlRegistry _registry;
    private readonly OutboxService _outbox;
    private readonly ControlHub _hub;

    /// Milestone S2 additions (optional trailing parameters so the S1 test harness still builds
    /// DiagnosticsService with its original six arguments).
    private readonly LiveSettings? _settings;
    private readonly LiveMetrics? _metrics;
    private readonly AnnouncementsService? _announcements;
    private readonly DateTimeOffset _startedAt;

    public DiagnosticsService(Db db, ServerConfig cfg, TimeProvider time, ControlRegistry registry,
        OutboxService outbox, ControlHub hub, LiveSettings? settings = null, LiveMetrics? metrics = null,
        AnnouncementsService? announcements = null)
    {
        _db = db; _cfg = cfg; _time = time; _registry = registry; _outbox = outbox; _hub = hub;
        _settings = settings; _metrics = metrics; _announcements = announcements;
        _startedAt = time.GetUtcNow();
    }

    public DiagnosticsSnapshot Snapshot()
    {
        var (outboxTotal, outboxMax) = _outbox.Depth();
        var dbSize = File.Exists(_db.DbPath) ? new FileInfo(_db.DbPath).Length : 0;
        long walSize = 0;
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var p = _db.DbPath + suffix;
            if (File.Exists(p)) walSize += new FileInfo(p).Length;
        }
        // Milestone S2: media relay queues, mic connections, flood/overflow counters and every
        // live setting, all visible here (plan §14: configurable and visible, never a hidden cap).
        var depths = _announcements?.MediaQueueDepths() ?? [];
        int queueMax = depths.Count > 0 ? depths.Max(d => d.MaxFrames) : 0;
        double queueMean = depths.Count > 0 ? Math.Round(depths.Average(d => d.MeanFrames), 2) : 0;
        int micConns = _announcements?.ActiveMicConnections() ?? _metrics?.MicConnections ?? 0;
        int activeAnnouncements = _announcements?.ActiveAnnouncementCount() ?? 0;

        return new DiagnosticsSnapshot(
            Util.ServerVersion,
            _cfg.ServerName,
            _startedAt.ToUnixTimeMilliseconds(),
            _time.GetUtcNow().ToUnixTimeMilliseconds(),
            (long)(_time.GetUtcNow() - _startedAt).TotalSeconds,
            _registry.Count,
            _cfg.MaxConnections,
            _hub.RejectedAtConnectionLimit,
            outboxTotal,
            outboxMax,
            _cfg.OutboxCap,
            _outbox.TotalDropped,
            dbSize,
            walSize,
            _cfg.StaleAfterSeconds,
            _db.GetSetting("school_tz") ?? "",
            micConns,
            activeAnnouncements,
            queueMax,
            queueMean,
            depths.Count,
            _metrics?.DroppedFrames ?? 0,
            _metrics?.QueueOverflowDrops ?? 0,
            _metrics?.FramesStreamed ?? 0,
            _metrics?.OversizeFrames ?? 0,
            _settings?.TranscriptionEnabled ?? false,
            Count("SELECT COUNT(*) FROM transcript_jobs WHERE status = 'queued'"),
            Count("SELECT COUNT(*) FROM transcript_jobs WHERE status = 'failed'"),
            _settings?.PublicView());
    }

    private int Count(string sql)
    {
        using var conn = _db.Open();
        return conn.ScalarInt(sql);
    }
}

public sealed record DiagnosticsSnapshot(
    string ServerVersion,
    string ServerName,
    long StartedAt,
    long NowUtc,
    long UptimeSeconds,
    int ActiveControlConnections,
    int MaxConnectionsConfigured,
    long RejectedAtConnectionLimit,
    long OutboxTotal,
    long OutboxMaxPerDevice,
    int OutboxCapConfigured,
    long OutboxDroppedTotal,
    long DbSizeBytes,
    long DbWalBytes,
    int StaleAfterSecondsConfigured,
    string SchoolTz,
    // ---- milestone S2 (append-only: existing consumers keep their fields) ----
    int ActiveMicConnections,
    int ActiveAnnouncements,
    int MediaQueueMaxFrames,
    double MediaQueueMeanFrames,
    int MediaQueueCount,
    long DroppedFramesTotal,
    long QueueOverflowDropsTotal,
    long FramesStreamedTotal,
    long OversizeFramesTotal,
    bool TranscriptionEnabled,
    int TranscriptJobsQueued,
    int TranscriptJobsFailed,
    object? LiveSettings);

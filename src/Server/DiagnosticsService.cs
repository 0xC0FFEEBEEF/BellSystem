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
    private readonly DateTimeOffset _startedAt;

    public DiagnosticsService(Db db, ServerConfig cfg, TimeProvider time, ControlRegistry registry,
        OutboxService outbox, ControlHub hub)
    {
        _db = db; _cfg = cfg; _time = time; _registry = registry; _outbox = outbox; _hub = hub;
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
            _db.GetSetting("school_tz") ?? "");
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
    string SchoolTz);

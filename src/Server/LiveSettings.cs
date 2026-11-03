using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

/// Settings for milestone S2 (announcements / live media / retention). Stored in the `settings`
/// table so /admin can change them at runtime; every value has a documented default and a clamp.
/// There is no hidden device cap: queue sizes are visible in /api/diagnostics.
public sealed class LiveSettings
{
    // Hard protocol bounds (PROTOCOL.md §10/§11) — configurable settings may not exceed them.
    public const int LeaseMinS = 30;
    public const int LeaseMaxCapS = 900;
    public const long RecordedMaxBytes = 25L * 1024 * 1024;   // 25 MB
    public const int RecordedMaxDurationS = 300;              // 5 min
    public const int TranscriptionMaxAttempts = 5;

    private readonly Db _db;
    private readonly ILogger<LiveSettings> _log;

    public LiveSettings(Db db, ILogger<LiveSettings>? log = null)
    {
        _db = db;
        _log = log ?? LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)).CreateLogger<LiveSettings>();
    }

    private int Int(string key, int fallback, int min, int max)
    {
        var raw = _db.GetSetting(key);
        if (!int.TryParse(raw, out var v)) return fallback;
        if (v < min || v > max)
        {
            _log.LogWarning("setting {Key}={Value} is outside {Min}..{Max}; using the clamped value", key, v, min, max);
            v = Math.Clamp(v, min, max);
        }
        return v;
    }

    private long Long(string key, long fallback, long min, long max)
    {
        var raw = _db.GetSetting(key);
        if (!long.TryParse(raw, out var v)) return fallback;
        return Math.Clamp(v, min, max);
    }

    /// Talk-lease max duration (PROTOCOL.md §7 default 300 s; settings cap 30..900 s).
    public int LeaseMaxS => Int("talk_lease_max_s", 300, LeaseMinS, LeaseMaxCapS);
    public int LeaseRenewIntervalS => Int("talk_lease_renew_interval_s", 60, 5, 600);

    /// Live offers expire quickly: an offline device must never receive stale live audio (PROTOCOL.md §7).
    public int LiveOfferTtlS => Int("live_offer_ttl_s", 5, 1, 60);
    public int PreparationWindowS => Int("preparation_window_s", 5, 1, 60);
    public int MediaTokenTtlS => Int("media_token_ttl_s", 10, 2, 120);

    /// Per-device bounded send queue (PROTOCOL.md §7: 1500 frames ≈ 30 s ≈ 7.7 MB).
    public int MediaQueueFrames => Int("media_queue_frames", 1500, 1, 100_000);

    /// Flood guard: more than this many frames/s sustained for mic_burst_s drops the excess.
    public int MicFramesPerS => Int("mic_frames_per_s", 60, 10, 500);
    public int MicBurstS => Int("mic_burst_s", 2, 1, 10);

    /// Recorded offers stay offerable for this long (plan §9 / PROTOCOL.md §11 default 24 h).
    public int RecordedOfferTtlS => Int("recorded_offer_ttl_s", 24 * 3600, 1, 7 * 24 * 3600);

    public int AudioRetentionDays => Int("audio_retention_days", 7, 0, 365);
    public int TranscriptRetentionDays => Int("transcript_retention_days", 30, 0, 365);

    public bool TranscriptionEnabled
    {
        get
        {
            var raw = _db.GetSetting("transcription_enabled");
            return raw is not null && (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// Everything visible in /api/diagnostics so no limit is hidden (plan §14).
    public object PublicView() => new
    {
        talk_lease_max_s = LeaseMaxS,
        talk_lease_renew_interval_s = LeaseRenewIntervalS,
        live_offer_ttl_s = LiveOfferTtlS,
        preparation_window_s = PreparationWindowS,
        media_token_ttl_s = MediaTokenTtlS,
        media_queue_frames = MediaQueueFrames,
        mic_frames_per_s = MicFramesPerS,
        mic_burst_s = MicBurstS,
        recorded_offer_ttl_s = RecordedOfferTtlS,
        audio_retention_days = AudioRetentionDays,
        transcript_retention_days = TranscriptRetentionDays,
        transcription_enabled = TranscriptionEnabled,
        recorded_max_bytes = RecordedMaxBytes,
        recorded_max_duration_s = RecordedMaxDurationS,
        transcription_max_attempts = TranscriptionMaxAttempts,
    };
}

/// Live counters for /api/diagnostics (milestone S2). Plain Interlocked fields: the media path
/// must not touch the database per frame.
public sealed class LiveMetrics
{
    private long _droppedFrames;          // flood-guard drops (office mic input)
    private long _queueOverflowDrops;     // per-device send-queue overflows
    private long _framesStreamed;         // frames written to device media sockets
    private long _oversizeFrames;         // SBMF frames rejected for payload bounds
    private long _floodEvents;
    private int _micConnections;

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public long QueueOverflowDrops => Interlocked.Read(ref _queueOverflowDrops);
    public long FramesStreamed => Interlocked.Read(ref _framesStreamed);
    public long OversizeFrames => Interlocked.Read(ref _oversizeFrames);
    public long FloodEvents => Interlocked.Read(ref _floodEvents);
    public int MicConnections => Volatile.Read(ref _micConnections);

    public void AddDroppedFrames(long n) => Interlocked.Add(ref _droppedFrames, n);
    public void AddQueueOverflow(long n) => Interlocked.Add(ref _queueOverflowDrops, n);
    public void AddFramesStreamed(long n) => Interlocked.Add(ref _framesStreamed, n);
    public void AddOversizeFrame() => Interlocked.Increment(ref _oversizeFrames);
    public void AddFloodEvent() => Interlocked.Increment(ref _floodEvents);
    public void MicConnected() => Interlocked.Increment(ref _micConnections);
    public void MicDisconnected() => Interlocked.Decrement(ref _micConnections);
}

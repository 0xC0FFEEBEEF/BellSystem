using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Device session orchestrator: one reconnect loop (no parallel loops), one bell loop
/// (runs offline from the accepted manifest), receipt flushing, status reporting and
/// keep-awake handling. Windows-specific effects enter only through the interfaces.
public sealed partial class DeviceAgent
{
    private readonly ClientConfig _cfg;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly IBellPlayer _bellPlayer;
    private readonly IAnnouncementPipeline _pipeline;
    private readonly IRendererProvider _renderer;
    private readonly IIdentityProvider _identity;
    private readonly IKeepAwakeApplier _keepAwake;
    private readonly IClientUpdater? _updater;
    private readonly byte[] _verifyKey;

    private readonly Channel<byte> _bellWake = Channel.CreateUnbounded<byte>();
    private readonly Channel<byte> _statusWake = Channel.CreateUnbounded<byte>();
    private readonly Channel<byte> _receiptWake = Channel.CreateUnbounded<byte>();

    public BellClock Clock { get; }
    public CursorStore Cursor { get; }
    public ClaimLedger Ledger { get; }
    public ReceiptQueue Receipts { get; }
    public ManifestStore Manifests { get; }
    public SoundCache Cache { get; }
    public KeepAwakePolicyStore KeepAwakePolicies { get; }
    public BellScheduler Scheduler { get; }
    public StatusComposer Status { get; }
    public DeviceSettingsStore Settings { get; }
    public HistoryStore History { get; }

    private volatile bool _statusDirty;
    private PlaybackResult? _lastPlayback;
    private long _staleCacheLoggedAtRevision = -1;

    public event Action<ScheduleManifestPayload>? ManifestAccepted;
    public event Action? StateChanged;
    public event Action<DeviceSettingsPayload>? SettingsChanged;
    public event Action? HistoryChanged;
    public event Action<bool>? ConnectionChanged;

    public bool Connected { get; private set; }
    public string ServerUrl => _cfg.ServerUrl;

    public DeviceAgent(ClientConfig cfg, TimeProvider time, HttpClient http, ILogger log,
        IBellPlayer bellPlayer, IAnnouncementPipeline pipeline, IRendererProvider renderer,
        IIdentityProvider identity, IKeepAwakeApplier keepAwake, IClientUpdater? updater = null)
    {
        _cfg = cfg; _time = time; _http = http; _log = log;
        if (_http.BaseAddress is null)
        {
            var u = new Uri(cfg.ServerUrl);
            _http.BaseAddress = new Uri($"{u.Scheme}://{u.Authority}"); // sounds use relative urls
        }
        _bellPlayer = bellPlayer; _pipeline = pipeline; _renderer = renderer;
        _identity = identity; _keepAwake = keepAwake; _updater = updater;
        _verifyKey = ManifestSigner.DeriveDeviceVerifyKey(cfg.ServerTrustPin);

        Clock = new BellClock(time);
        Cursor = new CursorStore(cfg.DataDir);
        Ledger = new ClaimLedger(cfg.DataDir);
        Receipts = new ReceiptQueue(cfg.DataDir, log);
        Manifests = new ManifestStore(cfg.DataDir);
        Cache = new SoundCache(cfg.DataDir);
        KeepAwakePolicies = new KeepAwakePolicyStore(cfg.DataDir);
        Scheduler = new BellScheduler(Ledger, cfg.MissedBellGraceSeconds);
        Status = new StatusComposer(Clock, Manifests);
        Settings = new DeviceSettingsStore(cfg.DataDir);
        History = new HistoryStore(cfg.DataDir);
    }

    /// Groups from the server's device.settings plus any the installer wrote into config.
    public IReadOnlyCollection<long> EffectiveGroups =>
        (Settings.Current?.Groups ?? []).Concat(_cfg.DeviceGroups).Distinct().ToList();

    /// Announcement level in percent: server setting (capped) or the config default.
    public int AnnouncementVolume
    {
        get
        {
            var s = Settings.Current;
            if (s is null) return _cfg.AnnouncementVolume;
            return Math.Clamp(Math.Min(s.AnnouncementVolume, s.VolumeCap <= 0 ? 100 : s.VolumeCap), 0, 100);
        }
    }

    public bool StaleCache
    {
        get
        {
            var m = Manifests.Current;
            return m is null || Clock.NowMs() > m.ExpiresAt;
        }
    }

    public long CacheExpiresAt => Manifests.Current?.ExpiresAt ?? 0;

    public void NotifyStateChanged()
    {
        StateChanged?.Invoke();
        if (_statusDirty) return;
        _statusDirty = true;
        _statusWake.Writer.TryWrite(0);
    }

    /// Next not-yet-claimed occurrence matching this device after nowMs (for tray display).
    public Occurrence? PeekNextOccurrence()
    {
        var m = Manifests.Current;
        if (m is null) return null;
        var now = Clock.NowMs();
        return m.Occurrences
            .Where(o => o.StartsAt > now && !Ledger.Has(o.OccurrenceId) &&
                        o.Target.Matches(_cfg.DeviceId, EffectiveGroups))
            .OrderBy(o => o.StartsAt)
            .FirstOrDefault();
    }

    /// Single device-session loop: bell loop runs regardless of connectivity; the
    /// control connection retries forever with exponential backoff + full jitter.
    public async Task RunAsync(CancellationToken ct)
    {
        RecoverUnknownClaims();
        if (_updater?.TakeCompletedResult() is { } finished)
            lock (_pendingUpdateStatuses) _pendingUpdateStatuses.Add(finished);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var bellLoop = Task.Run(() => BellLoopAsync(linked.Token), linked.Token);

        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(_cfg.ReconnectCapSeconds));
        int cycle = 0;
        while (!ct.IsCancellationRequested)
        {
            cycle++;
            long stableMs = 0;
            try
            {
                stableMs = await ConnectAndServeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketProtocolFailedException e)
            {
                // Auth/revocation failures are diagnostic, not transient (PROTOCOL §10):
                // keep retrying on the bounded schedule but surface the reason clearly.
                _log.LogError("control session cycle {Cycle} rejected by server: {Reason} (device {DeviceId})",
                    cycle, e.Reason, _cfg.DeviceId);
            }
            catch (Exception e)
            {
                _log.LogWarning("control session cycle {Cycle} ended: {Error} (reconnecting; secrets never logged)",
                    cycle, e.Message);
            }

            policy.ReportStableConnection(stableMs); // resets attempt growth after ≥60 s stable
            var delay = policy.NextDelay();
            _log.LogInformation("reconnect attempt {Attempt} in {DelayMs:F0} ms", policy.Attempt, delay.TotalMilliseconds);
            try { await _time.DelayAsync(delay, ct); }
            catch (OperationCanceledException) { break; }
        }

        linked.Cancel();
        try { await bellLoop; } catch (OperationCanceledException) { }
    }

    /// Returns how long the connection stayed up (monotonic ms); RunAsync uses this to
    /// reset backoff growth once a session was stable for ≥60 s.
    private async Task<long> ConnectAndServeAsync(CancellationToken ct)
    {
        await using var session = await ControlSession.ConnectAsync(_cfg, _time, _log, ct);
        Clock.Sync(session.AuthOk!.ServerTime);
        var connectedStamp = _time.GetTimestamp();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Resync header: durable cursor + cache revision + the oldest queued receipts. The server
        // reads at most 32 KB from a device, so the header carries 100 (~15 KB); the rest follow as
        // ordinary receipts. (A computer offline for days used to queue enough to never reconnect.)
        var queued = Receipts.DequeueBatch(ResyncHeaderReceipts);
        try
        {
            var resync = queued
                .Select(r => new QueuedReceipt(r.OccurrenceOrAnnouncementId, r.State, r.At, r.ErrorCode))
                .ToList();
            await session.SendAsync(ControlEnvelope.Create(Protocol.TDeviceResync,
                PayloadSerializer.Node(new DeviceResyncPayload(Cursor.Cursor, Manifests.LocalRevision, resync))),
                cts.Token);
        }
        catch
        {
            Receipts.PrependBack(queued);
            throw;
        }

        Connected = true;
        ConnectionChanged?.Invoke(true);
        _currentSession = session;
        await FlushUpdateStatusesAsync(session, cts.Token);

        var receiveTask = ReceiveLoopAsync(session, cts.Token);
        var flushTask = ReceiptFlushLoopAsync(session, cts.Token);
        var statusTask = StatusLoopAsync(session, cts.Token);

        // Any app-level receive error (or socket close) ends the session; a half-open
        // socket surfaces through the periodic status/receipt sends.
        Exception? receiveError = null;
        try { await receiveTask; }
        catch (OperationCanceledException) { }
        catch (Exception e) { receiveError = e; }

        cts.Cancel();
        _currentSession = null;
        Connected = false;
        ConnectionChanged?.Invoke(false);
        await CloseOpenAnnouncementsAsync(ct); // restore volume immediately on any session end
        var stableMs = (long)_time.GetElapsedTime(connectedStamp).TotalMilliseconds;
        await session.CloseAsync(WebSocketCloseStatus.NormalClosure, "session end");
        await SafeJoin(flushTask);
        await SafeJoin(statusTask);

        if (receiveError is not null) throw receiveError;
        return stableMs;
    }

    private static async Task SafeJoin(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch { /* session already failing; primary error reported by caller */ }
    }

    /// Crash window recovery: claims with no terminal state become ONE `unknown`
    /// receipt each (never claim audible exactly-once). Runs at service start.
    private void RecoverUnknownClaims()
    {
        var now = Clock.NowMs();
        foreach (var claim in Ledger.UnterminalClaims())
        {
            Ledger.MarkTerminal(claim.OccurrenceId, ReceiptState.Unknown);
            Receipts.Enqueue(new ReceiptPayload(claim.OccurrenceId, ReceiptState.Unknown, claim.ClaimedAt, "crash_recovery"));
            _log.LogWarning("recovered unterminated claim for occurrence {OccurrenceId} as unknown receipt",
                claim.OccurrenceId);
        }
        if (Receipts.Count > 0) _receiptWake.Writer.TryWrite(0);
    }

    private async Task BellLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long delayMs;
            try
            {
                var now = Clock.NowMs();
                var ev = Scheduler.Evaluate(Manifests.Current, now, LiveAnnouncementActive,
                    _cfg.DeviceId, EffectiveGroups);

                if (ev.StaleCache)
                {
                    var rev = Manifests.LocalRevision;
                    if (_staleCacheLoggedAtRevision != rev)
                    {
                        _staleCacheLoggedAtRevision = rev;
                        _log.LogWarning("schedule cache stale (revision {Revision} expired) — scheduling stopped, not guessing the calendar", rev);
                        NotifyStateChanged();
                    }
                    delayMs = 30_000;
                }
                else
                {
                    foreach (var d in ev.Decisions.Where(d => d.Action != BellAction.Play))
                        await ExecuteBellAsync(d, ct);
                    // bells due at the same moment (overlapping schedules for different groups that
                    // both include this computer) ring once; each records the same result
                    foreach (var same in ev.Decisions.Where(d => d.Action == BellAction.Play).GroupBy(d => d.Occurrence.StartsAt))
                    {
                        var ordered = same.OrderBy(d => d.Occurrence.Kind == "start" ? 0 : d.Occurrence.Kind == "warn" ? 1 : 2)
                            .ThenByDescending(d => d.Occurrence.VolumePolicy == "announcement")
                            .ThenByDescending(d => d.Occurrence.DurationS).ToList();
                        var (state, reason) = await ExecuteBellAsync(ordered[0], ct);
                        foreach (var other in ordered.Skip(1))
                        {
                            Ledger.MarkTerminal(other.OccurrenceId, state);
                            QueueReceipt(new ReceiptPayload(other.OccurrenceId, state, Clock.NowMs(), reason));
                            _log.LogInformation("bell '{Bell}' ({OccurrenceId}) is at the same moment as '{Played}' — rang once -> {State}",
                                other.Occurrence.BellName, other.OccurrenceId, ordered[0].Occurrence.BellName, state);
                        }
                    }
                    // measure from NOW: playing the bells above took seconds, and waking that much
                    // late put the next bell past its grace window (every other bell expired)
                    delayMs = ev.NextDueMs is long due
                        ? Math.Clamp(due - Clock.NowMs(), 50, 6 * 3_600_000L)
                        : 30_000;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.LogError(e, "bell loop iteration failed");
                delayMs = 10_000;
            }

            try
            {
                var timer = _time.DelayAsync(TimeSpan.FromMilliseconds(delayMs), ct);
                var wake = _bellWake.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(timer, wake);
                while (_bellWake.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<(string State, string? Reason)> ExecuteBellAsync(BellDecision decision, CancellationToken ct)
    {
        var occ = decision.Occurrence;
        var now = Clock.NowMs();
        switch (decision.Action)
        {
            case BellAction.Expired:
                Ledger.MarkTerminal(decision.OccurrenceId, ReceiptState.Expired);
                QueueReceipt(new ReceiptPayload(decision.OccurrenceId, ReceiptState.Expired, now, "missed_bell"));
                _log.LogInformation("bell '{Bell}' ({OccurrenceId}) missed past grace — recorded expired, not replayed",
                    occ.BellName, decision.OccurrenceId);
                return (ReceiptState.Expired, "missed_bell");

            case BellAction.Suppressed:
                Ledger.MarkTerminal(decision.OccurrenceId, ReceiptState.Suppressed);
                QueueReceipt(new ReceiptPayload(decision.OccurrenceId, ReceiptState.Suppressed, now, "live_announcement"));
                _log.LogInformation("bell '{Bell}' ({OccurrenceId}) suppressed by live announcement",
                    occ.BellName, decision.OccurrenceId);
                return (ReceiptState.Suppressed, "live_announcement");

            case BellAction.Play:
                var wav = Cache.GetSoundPath(occ.SoundId);
                if (wav is null)
                {
                    Ledger.MarkTerminal(decision.OccurrenceId, ReceiptState.Failed);
                    QueueReceipt(new ReceiptPayload(decision.OccurrenceId, ReceiptState.Failed, now, "sound_missing"));
                    _log.LogWarning("bell '{Bell}' ({OccurrenceId}): cached sound '{SoundId}' missing — failed",
                        occ.BellName, decision.OccurrenceId, occ.SoundId);
                    return (ReceiptState.Failed, "sound_missing");
                }
                var req = new PipeBellPlay(decision.OccurrenceId, occ.BellName, wav, occ.DurationS,
                    _cfg.BellFadeMs, occ.VolumePolicy,
                    occ.VolumePolicy == "announcement" ? AnnouncementVolume : 0);
                var outcome = await PlayWithRetryAsync(req,
                    Math.Min(occ.StartsAt + BellRetryWindowMs, occ.ExpiresAt), ct);
                var state = outcome.StateOverride ?? (outcome.Played ? ReceiptState.Completed : ReceiptState.Failed);
                Ledger.MarkTerminal(decision.OccurrenceId, state);
                QueueReceipt(new ReceiptPayload(decision.OccurrenceId, state, Clock.NowMs(), outcome.Reason));
                _lastPlayback = new PlaybackResult(decision.OccurrenceId, state, Clock.NowMs());
                _log.LogInformation("bell '{Bell}' ({OccurrenceId}) -> {State}", occ.BellName, decision.OccurrenceId, state);
                NotifyStateChanged();
                return (state, outcome.Reason);
        }
        return (ReceiptState.Unknown, null);
    }

    /// Queued results sent in the reconnect header (the server reads at most 32 KB from a device).
    public const int ResyncHeaderReceipts = 100;

    /// A bell that did not play for a passing reason (speaker briefly gone, tray app restarting,
    /// playback error) is tried again, but only within 4 s of its time: later than that it no longer
    /// marks the moment. Missing or unreadable sounds are not retried: another try cannot fix them.
    internal const long BellRetryWindowMs = 4_000;
    internal static readonly int[] BellRetryDelaysMs = [1_000, 2_500];
    internal static bool RetryableBellFailure(string? reason) =>
        reason is "renderer" or "renderer_timeout" or "renderer_lost" or "output_missing" or "no_renderer";

    private async Task<BellPlayOutcome> PlayWithRetryAsync(PipeBellPlay req, long retryUntilMs, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            BellPlayOutcome outcome;
            try { outcome = await _bellPlayer.PlayAsync(req, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _log.LogWarning(e, "bell playback instruction failed for {OccurrenceId}", req.OccurrenceId);
                outcome = new(false, "renderer");
            }
            if (outcome.Played)
                return attempt == 0 || outcome.Reason is not null ? outcome : outcome with { Reason = $"played_on_retry_{attempt}" };
            if (!RetryableBellFailure(outcome.Reason) || attempt >= BellRetryDelaysMs.Length) return outcome;
            var delay = BellRetryDelaysMs[attempt];
            if (Clock.NowMs() + delay > retryUntilMs) return outcome;
            _log.LogWarning("bell '{Bell}' ({OccurrenceId}) did not play ({Reason}); trying again in {Delay} ms ({Attempt} of {Max})",
                req.BellName, req.OccurrenceId, outcome.Reason, delay, attempt + 1, BellRetryDelaysMs.Length);
            await _time.DelayAsync(TimeSpan.FromMilliseconds(delay), ct);
            if (LiveAnnouncementActive) return new(false, "live_announcement", ReceiptState.Suppressed);
        }
    }

    public void QueueReceipt(ReceiptPayload receipt)
    {
        Receipts.Enqueue(receipt);
        _receiptWake.Writer.TryWrite(0);
    }

    private async Task ReceiptFlushLoopAsync(ControlSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = Receipts.DequeueBatch();
                if (batch.Count == 0)
                {
                    // wait for a new receipt or the periodic 15 s nudge
                    var wake = _receiptWake.Reader.WaitToReadAsync(ct).AsTask();
                    var timer = _time.DelayAsync(TimeSpan.FromSeconds(15), ct);
                    await Task.WhenAny(wake, timer);
                    while (_receiptWake.Reader.TryRead(out _)) { }
                    continue;
                }
                try
                {
                    foreach (var r in batch)
                        await session.SendAsync(ControlEnvelope.Create(Protocol.TReceipt, PayloadSerializer.Node(r)), ct);
                }
                catch
                {
                    Receipts.PrependBack(batch);
                    return; // connection is failing; reconnect will resync the queue
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task StatusLoopAsync(ControlSession session, CancellationToken ct)
    {
        var lastSentMs = 0L;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = Clock.NowMs();
                if (_statusDirty && now - lastSentMs >= 1_000)
                {
                    _statusDirty = false;
                    await session.SendAsync(ControlEnvelope.Create(Protocol.TDeviceStatus, PayloadSerializer.Node(BuildStatus())), ct);
                    lastSentMs = now;
                }
                var wake = _statusWake.Reader.WaitToReadAsync(ct).AsTask();
                var timer = _time.DelayAsync(TimeSpan.FromSeconds(_cfg.StatusReportSeconds), ct);
                await Task.WhenAny(wake, timer);
                while (_statusWake.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                _log.LogWarning("status report send failed: {Error}", e.Message);
                return;
            }
        }
    }

    public DeviceStatusPayload BuildStatus()
    {
        var (user, sid) = _identity.GetActiveUser();
        var kaState = KeepAwakePolicies.Evaluate(Clock.NowMs());
        var facts = new StatusFacts(
            Environment.MachineName, user, sid,
            _renderer.GetRenderer(), _renderer.GetVolumes(),
            kaState, _keepAwake.Applied, _lastPlayback, _renderer.CompanionState,
            _renderer.AvailableOutputs.Take(16).ToList(), _renderer.RendererOwner);
        return Status.Build(facts, CacheExpiresAt);
    }

    public void WakeBellLoop() => _bellWake.Writer.TryWrite(0);
}

/// Server closed the protocol path (bad proof, revoked, unknown device). Distinct from
/// transport errors so the log can label permanent auth failures honestly.
public sealed class WebSocketProtocolFailedException : Exception
{
    public string Reason { get; }
    public WebSocketProtocolFailedException(string reason, string message) : base(message) => Reason = reason;
}

using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public sealed partial class DeviceAgent
{
    private sealed class AnnContext
    {
        public required AnnouncementOfferPayload Offer { get; init; }
        public required JitterBuffer Jitter { get; init; }
        public MediaStreamClient? Media { get; set; }
        public bool StartedReceipted { get; set; }
        public bool Finished { get; set; }
    }

    private readonly Dictionary<string, AnnContext> _announcements = [];

    /// Media silence that ends an announcement locally (frames flow continuously while the
    /// office holds the talk button, so a gap this long means a lost stream).
    public int StreamStallMs { get; set; } = 6_000;
    public int FirstFrameTimeoutMs { get; set; } = 15_000;
    private readonly object _annGate = new();

    /// Live-speech priority (PROTOCOL §6/§7): while an announcement for this device is
    /// open (ready → end), due bells are suppressed and never ring later.
    public bool LiveAnnouncementActive
    {
        get { lock (_annGate) return _announcements.Any(kv => !kv.Value.Finished); }
    }

    private async Task<bool> HandleOfferAsync(ControlSession session, ControlEnvelope env, CancellationToken ct)
    {
        var offer = PayloadSerializer.ToPayload<AnnouncementOfferPayload>(env);
        if (offer is null || string.IsNullOrEmpty(offer.AnnouncementId)) return true;

        var now = Clock.NowMs();
        // A recorded announcement that already played to completion here is never played again
        // (e.g. an outbox replay after a service restart). Failed attempts may retry within expiry.
        if (Ledger.Has(offer.AnnouncementId))
        {
            await session.SendAsync(ControlEnvelope.Create(Protocol.TAnnouncementReject,
                PayloadSerializer.Node(new AnnouncementRejectPayload(offer.AnnouncementId, "already_played"))), ct);
            return true;
        }
        lock (_annGate)
            if (_announcements.ContainsKey(offer.AnnouncementId)) return true; // duplicate offer in flight
        var renderer = _renderer.GetRenderer();
        var (ready, reason) = AnnouncementGate.Decide(offer, now, renderer);
        if (!ready)
        {
            await session.SendAsync(ControlEnvelope.Create(Protocol.TAnnouncementReject,
                PayloadSerializer.Node(new AnnouncementRejectPayload(offer.AnnouncementId, reason!))), ct);
            _log.LogInformation("announcement {AnnouncementId} rejected: {Reason}", offer.AnnouncementId, reason);
            return true;
        }

        var ctx = new AnnContext
        {
            Offer = offer,
            Jitter = new JitterBuffer(_cfg.JitterBufferStartMs, _cfg.JitterBufferMaxMs),
        };
        lock (_annGate) _announcements[offer.AnnouncementId] = ctx;

        await session.SendAsync(ControlEnvelope.Create(Protocol.TAnnouncementReady,
            PayloadSerializer.Node(new AnnouncementReadyPayload(offer.AnnouncementId, renderer.OutputDevice ?? ""))), ct);
        QueueReceipt(new ReceiptPayload(offer.AnnouncementId, ReceiptState.Ready, Clock.NowMs(), null));

        // Companion prepares output + volume override now (before the preparation window ends).
        await _pipeline.StartAsync(new PipeAnnStart(
            offer.AnnouncementId, offer.Kind, offer.SourceLabel, offer.Chime,
            "announcement", AnnouncementVolume, offer.MaxDurationS), ct);

        NotifyStateChanged(); // liveActive now suppresses due bells
        _ = WatchdogAsync(offer.AnnouncementId, offer.MaxDurationS + 5, ct);
        return true;
    }

    private async Task WatchdogAsync(string announcementId, int seconds, CancellationToken ct)
    {
        try { await _time.DelayAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600)), ct); }
        catch (OperationCanceledException) { return; }
        lock (_annGate)
        {
            if (!_announcements.TryGetValue(announcementId, out _) || _announcements[announcementId].Finished) return;
        }
        _log.LogWarning("announcement {AnnouncementId} watchdog fired after {Seconds}s — stopping and restoring volume",
            announcementId, seconds);
        await FinishAnnouncementAsync(announcementId, "timeout", ReceiptState.Failed, "watchdog_timeout", ct);
    }

    private async Task<bool> HandleMediaTokenAsync(ControlSession session, ControlEnvelope env, CancellationToken ct)
    {
        var payload = PayloadSerializer.ToPayload<MediaTokenPayload>(env);
        if (payload is null || string.IsNullOrEmpty(payload.AnnouncementId)) return true;

        AnnContext? ctx;
        lock (_annGate) _announcements.TryGetValue(payload.AnnouncementId, out ctx);
        if (ctx is null || ctx.Finished)
        {
            _log.LogWarning("media.token for unknown/finished announcement {AnnouncementId}", payload.AnnouncementId);
            return true;
        }
        if (payload.ExpiresAt <= Clock.NowMs())
        {
            _log.LogWarning("media.token for {AnnouncementId} already expired — not connecting (no secret logged)",
                payload.AnnouncementId);
            await FinishAnnouncementAsync(payload.AnnouncementId, "error", ReceiptState.Failed, "media_token_expired", ct);
            return true;
        }

        try
        {
            ctx.Media = await MediaStreamClient.ConnectAsync(_cfg, payload.AnnouncementId, payload.Token, _time, _log, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log.LogWarning("media channel connect failed for {AnnouncementId}: {Error}", payload.AnnouncementId, e.Message);
            await FinishAnnouncementAsync(payload.AnnouncementId, "error", ReceiptState.Failed, "media_connect", ct);
            return true;
        }

        _ = MediaLoopAsync(ctx, ct);
        return true;
    }

    private async Task MediaLoopAsync(AnnContext ctx, CancellationToken ct)
    {
        var id = ctx.Offer.AnnouncementId;
        var batch = new List<MediaFrame>();
        bool sawLast = false, stalled = false;
        try
        {
            while (ctx.Media is not null && !ct.IsCancellationRequested)
            {
                // Stall detection: a live stream that goes quiet stops locally within seconds
                // (volume restored) instead of waiting for the max-duration watchdog.
                var stallMs = ctx.StartedReceipted ? StreamStallMs : FirstFrameTimeoutMs;
                MediaFrame? frame;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    stall.CancelAfter(stallMs);
                    try { frame = await ctx.Media.ReceiveFrameAsync(stall.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _log.LogWarning("announcement {AnnouncementId}: no media for {Ms} ms — stopping locally", id, stallMs);
                        stalled = true;
                        break;
                    }
                }
                if (frame is null) break;
                if (!ctx.StartedReceipted)
                {
                    ctx.StartedReceipted = true;
                    QueueReceipt(new ReceiptPayload(id, ReceiptState.Started, Clock.NowMs(), null));
                }
                // The service only sequences (drops duplicates/stale frames, counts gaps) and
                // forwards at once; the renderer owns the timing jitter buffer, paced by the
                // audio clock. Holding frames here would add latency without smoothing.
                ctx.Jitter.Push(frame.Value);
                foreach (var ready in ctx.Jitter.Drain()) batch.Add(ready);
                ctx.Jitter.Reopen();
                if (batch.Count > 0)
                {
                    await _pipeline.FramesAsync(id, batch, ct);
                    batch.Clear();
                }
                if (frame.Value.IsLast) { sawLast = true; break; }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            _log.LogWarning("media loop error for {AnnouncementId}: {Error}", id, e.Message);
        }

        if (batch.Count > 0)
        {
            try { await _pipeline.FramesAsync(id, batch, CancellationToken.None); }
            catch (Exception e) { _log.LogWarning("final frame batch failed: {Error}", e.Message); }
        }

        if (stalled)
        {
            await FinishAnnouncementAsync(id, "timeout", ReceiptState.Failed, "stream_stalled", CancellationToken.None);
        }
        else if (sawLast)
        {
            await FinishAnnouncementAsync(id, "server",
                ctx.StartedReceipted ? ReceiptState.Completed : ReceiptState.Failed,
                ctx.StartedReceipted ? null : "no_media", CancellationToken.None);
        }
        else
        {
            // Media socket closed without the last flag: announcement.end (control) or the
            // watchdog decides the outcome — never re-request old frames.
            _log.LogInformation("media channel closed without last-frame flag for {AnnouncementId}; awaiting announcement.end/watchdog", id);
        }
    }

    private async Task<bool> HandleAnnouncementEndAsync(ControlSession session, ControlEnvelope env, CancellationToken ct)
    {
        var payload = PayloadSerializer.ToPayload<AnnouncementEndPayload>(env);
        if (payload is null || string.IsNullOrEmpty(payload.AnnouncementId)) return true;

        AnnContext? ctx;
        lock (_annGate) _announcements.TryGetValue(payload.AnnouncementId, out ctx);
        if (ctx is null || ctx.Finished) return true;

        await FinishAnnouncementAsync(payload.AnnouncementId, payload.EndedBy,
            ctx.StartedReceipted ? ReceiptState.Completed : ReceiptState.Failed,
            ctx.StartedReceipted ? null : "no_media", ct);
        return true;
    }

    /// One terminal outcome per announcement: drain valid audio, tell the companion to
    /// restore volume, record the receipt. Never plays a delayed backlog afterwards.
    private async Task FinishAnnouncementAsync(
        string announcementId, string endedBy, string state, string? errorCode, CancellationToken ct)
    {
        AnnContext ctx;
        lock (_annGate)
        {
            if (!_announcements.TryGetValue(announcementId, out var found) || found.Finished) return;
            found.Finished = true;
            _announcements.Remove(announcementId);
            ctx = found;
        }

        if (ctx.Media is not null)
            await ctx.Media.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "announcement end");

        var drained = ctx.Jitter.Drain();
        if (drained.Count > 0)
        {
            try { await _pipeline.FramesAsync(announcementId, drained, CancellationToken.None); }
            catch (Exception e) { _log.LogWarning("drain batch failed for {AnnouncementId}: {Error}", announcementId, e.Message); }
        }

        try { await _pipeline.EndAsync(announcementId, endedBy, CancellationToken.None); }
        catch (Exception e) { _log.LogWarning("pipeline end failed for {AnnouncementId}: {Error}", announcementId, e.Message); }

        if (state == ReceiptState.Completed && Ledger.TryClaim(announcementId, Clock.NowMs()))
            Ledger.MarkTerminal(announcementId, ReceiptState.Completed);
        QueueReceipt(new ReceiptPayload(announcementId, state, Clock.NowMs(), errorCode));
        _lastPlayback = new PlaybackResult(announcementId, state, Clock.NowMs());
        NotifyStateChanged();
        _log.LogInformation("announcement {AnnouncementId} finished: {State} (ended_by={EndedBy})",
            announcementId, state, endedBy);
    }

    /// Connection died mid-announcement: stop rendering and restore volume immediately.
    private async Task CloseOpenAnnouncementsAsync(CancellationToken ct)
    {
        List<string> open;
        lock (_annGate) open = _announcements.Where(kv => !kv.Value.Finished).Select(kv => kv.Key).ToList();
        foreach (var id in open)
            await FinishAnnouncementAsync(id, "error", ReceiptState.Failed, "connection_lost", ct);
    }
}

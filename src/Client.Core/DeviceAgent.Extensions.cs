using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Protocol §13 additions: ring now, device settings, tray history, client updates.
public sealed partial class DeviceAgent
{
    private ControlSession? _currentSession;
    private readonly List<UpdateStatusPayload> _pendingUpdateStatuses = [];
    private int _updateRunning;

    // ---------------------------------------------------------------- bell.ring (ring now)

    private bool HandleBellRing(ControlEnvelope env, CancellationToken ct)
    {
        var ring = PayloadSerializer.ToPayload<BellRingPayload>(env);
        if (ring is null || string.IsNullOrEmpty(ring.RingId) || ring.Sound is null) return true;
        var now = Clock.NowMs();

        // Claim first (durable): a replayed or duplicated ring finds the claim and does nothing.
        if (!Ledger.TryClaim(ring.RingId, now)) return true;
        if (now > ring.ExpiresAt)
        {
            Ledger.MarkTerminal(ring.RingId, ReceiptState.Expired);
            QueueReceipt(new ReceiptPayload(ring.RingId, ReceiptState.Expired, now, "ring_expired"));
            _log.LogInformation("ring {RingId} arrived {Late} ms after its expiry — recorded expired, not rung late",
                ring.RingId, now - ring.ExpiresAt);
            return true;
        }
        if (LiveAnnouncementActive)
        {
            Ledger.MarkTerminal(ring.RingId, ReceiptState.Suppressed);
            QueueReceipt(new ReceiptPayload(ring.RingId, ReceiptState.Suppressed, now, "live_announcement"));
            return true;
        }
        // Play off the receive loop so a 3-second bell never delays an announcement offer.
        _ = Task.Run(() => PlayRingAsync(ring, ct), CancellationToken.None);
        return true;
    }

    private async Task PlayRingAsync(BellRingPayload ring, CancellationToken ct)
    {
        string state; string? error = null;
        try
        {
            var wav = await Cache.EnsureSoundAsync(_http, ring.Sound, ct);
            if (wav is null)
            {
                state = ReceiptState.Failed; error = "sound_missing";
            }
            else if (Clock.NowMs() > ring.ExpiresAt)
            {
                state = ReceiptState.Expired; error = "ring_expired";
            }
            else
            {
                QueueReceipt(new ReceiptPayload(ring.RingId, ReceiptState.Started, Clock.NowMs(), null));
                var req = new PipeBellPlay(ring.RingId, ring.BellName, wav, ring.DurationS, _cfg.BellFadeMs,
                    ring.VolumePolicy, ring.VolumePolicy == "announcement" ? AnnouncementVolume : 0);
                var outcome = await PlayWithRetryAsync(req, Math.Min(Clock.NowMs() + BellRetryWindowMs, ring.ExpiresAt), ct);
                state = outcome.StateOverride ?? (outcome.Played ? ReceiptState.Completed : ReceiptState.Failed);
                error = outcome.Reason;
            }
        }
        catch (OperationCanceledException) { state = ReceiptState.Unknown; error = "service_stopping"; }
        catch (Exception e)
        {
            _log.LogWarning("ring {RingId} playback failed: {Error}", ring.RingId, e.Message);
            state = ReceiptState.Failed; error = "renderer";
        }
        Ledger.MarkTerminal(ring.RingId, state);
        QueueReceipt(new ReceiptPayload(ring.RingId, state, Clock.NowMs(), error));
        _lastPlayback = new PlaybackResult(ring.RingId, state, Clock.NowMs());
        _log.LogInformation("ring '{Bell}' ({RingId}) by {By} -> {State}", ring.BellName, ring.RingId, ring.RequestedBy, state);
        NotifyStateChanged();
    }

    // ---------------------------------------------------------------- device.settings

    private bool HandleDeviceSettings(ControlEnvelope env)
    {
        var settings = PayloadSerializer.ToPayload<DeviceSettingsPayload>(env);
        if (settings is null) return true;
        if (Settings.Apply(settings))
        {
            _log.LogInformation("device settings v{Version}: output '{Output}', announcement volume {Volume} (cap {Cap}), {Groups} group(s)",
                settings.SettingsVersion, settings.OutputDevice ?? "(Windows default)", settings.AnnouncementVolume,
                settings.VolumeCap, settings.Groups?.Count ?? 0);
            SettingsChanged?.Invoke(Settings.Current!);
            WakeBellLoop();          // group membership can change which bells match
            NotifyStateChanged();
        }
        return true;
    }

    // ---------------------------------------------------------------- history.item

    private bool HandleHistoryItem(ControlEnvelope env)
    {
        var item = PayloadSerializer.ToPayload<HistoryItemPayload>(env);
        if (item is null || string.IsNullOrEmpty(item.AnnouncementId)) return true;
        // Defense in depth: the server already omits text for targeted announcements.
        if (!item.Schoolwide) item = item with { Text = null };
        History.Upsert(item);
        HistoryChanged?.Invoke();
        return true;
    }

    private readonly Dictionary<string, long> _transcriptNotifications = [];

    private bool HandleTranscriptNotification(ControlEnvelope env)
    {
        var notice = PayloadSerializer.ToPayload<TranscriptNotificationPayload>(env);
        var now = Clock.NowMs();
        if (notice?.Item is not { Schoolwide: true, TranscriptStatus: "done" } item ||
            string.IsNullOrEmpty(notice.NotificationId) || string.IsNullOrEmpty(item.AnnouncementId) ||
            string.IsNullOrWhiteSpace(item.Text) || notice.ExpiresAt <= now) return true;
        foreach (var id in _transcriptNotifications.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
            _transcriptNotifications.Remove(id);
        if (_transcriptNotifications.ContainsKey(notice.NotificationId)) return true;
        if (_transcriptNotifications.Count >= 256) _transcriptNotifications.Remove(_transcriptNotifications.MinBy(p => p.Value).Key);
        _transcriptNotifications[notice.NotificationId] = notice.ExpiresAt;
        History.Upsert(item);
        HistoryChanged?.Invoke();
        TranscriptNotificationRequested?.Invoke(notice);
        return true;
    }

    private bool HandleAnnouncementTranscript(ControlEnvelope env)
    {
        var update = PayloadSerializer.ToPayload<AnnouncementTranscriptPayload>(env);
        if (update is null || string.IsNullOrEmpty(update.AnnouncementId)) return true;
        // Defense in depth: targeted transcript text is never exposed to the desktop session.
        if (!update.Schoolwide) update = update with { Text = null };
        AnnouncementTranscriptReceived?.Invoke(update);
        return true;
    }

    /// Called when the active console user changes (shared computers).
    public void OnActiveUserChanged()
    {
        if (History.DropTargeted()) HistoryChanged?.Invoke();
        NotifyStateChanged();
    }

    // ---------------------------------------------------------------- update.offer

    private bool HandleUpdateOffer(ControlEnvelope env, CancellationToken ct)
    {
        var offer = PayloadSerializer.ToPayload<UpdateOfferPayload>(env);
        if (offer is null || string.IsNullOrEmpty(offer.Version)) return true;
        if (_updater is null)
        {
            _ = ReportUpdateStatusAsync(new UpdateStatusPayload(offer.Version, "failed", "updater_unavailable", Clock.NowMs()));
            return true;
        }
        if (Interlocked.CompareExchange(ref _updateRunning, 1, 0) != 0)
        {
            _log.LogInformation("update.offer {Version} ignored: an update is already in progress", offer.Version);
            return true;
        }
        _ = Task.Run(async () =>
        {
            try { await _updater.HandleOfferAsync(offer, ReportUpdateStatusAsync, ct); }
            catch (Exception e)
            {
                _log.LogWarning("client update {Version} failed: {Error}", offer.Version, e.Message);
                await ReportUpdateStatusAsync(new UpdateStatusPayload(offer.Version, "failed", "exception", Clock.NowMs()));
            }
            finally { Interlocked.Exchange(ref _updateRunning, 0); }
        }, CancellationToken.None);
        return true;
    }

    /// True when installing now would not cut off a bell or an announcement.
    public bool IsQuietPeriod(int lookaheadSeconds = 120)
    {
        if (LiveAnnouncementActive) return false;
        var next = PeekNextOccurrence();
        return next is null || next.StartsAt - Clock.NowMs() > lookaheadSeconds * 1000L;
    }

    public async Task ReportUpdateStatusAsync(UpdateStatusPayload status)
    {
        _log.LogInformation("client update {Version}: {State} {Error}", status.Version, status.State, status.Error ?? "");
        var session = _currentSession;
        if (session is not null)
        {
            try
            {
                await session.SendAsync(ControlEnvelope.Create(Protocol.TUpdateStatus, PayloadSerializer.Node(status)),
                    CancellationToken.None);
                return;
            }
            catch (Exception) { /* fall through: keep it for the next connection */ }
        }
        lock (_pendingUpdateStatuses)
        {
            _pendingUpdateStatuses.Add(status);
            if (_pendingUpdateStatuses.Count > 20) _pendingUpdateStatuses.RemoveAt(0);
        }
    }

    private async Task FlushUpdateStatusesAsync(ControlSession session, CancellationToken ct)
    {
        List<UpdateStatusPayload> pending;
        lock (_pendingUpdateStatuses)
        {
            pending = _pendingUpdateStatuses.ToList();
            _pendingUpdateStatuses.Clear();
        }
        foreach (var s in pending)
            await session.SendAsync(ControlEnvelope.Create(Protocol.TUpdateStatus, PayloadSerializer.Node(s)), ct);
    }
}

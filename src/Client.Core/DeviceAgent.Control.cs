using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public sealed partial class DeviceAgent
{
    private readonly HashSet<string> _processedEventIds = new();

    private async Task ReceiveLoopAsync(ControlSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && session.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var env = await session.ReceiveEnvelopeAsync(ct);
            if (env is null) return; // socket closed by server
            await DispatchAsync(session, env, ct);
        }
    }

    /// Applies each event at-most-once; the durable cursor advances only for events we
    /// durably processed (a manifest whose sound download failed must be re-delivered).
    private async Task DispatchAsync(ControlSession session, ControlEnvelope env, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(env.EventId) && _processedEventIds.Contains(env.EventId!))
        {
            Cursor.Advance(env.EventId);
            return;
        }

        bool processed = env.Type switch
        {
            Protocol.TScheduleManifest => await HandleManifestAsync(session, env, ct),
            Protocol.TKeepAwakeSet => await HandleKeepAwakeSetAsync(session, env, ct),
            Protocol.TKeepAwakeClear => await HandleKeepAwakeClearAsync(session, env, ct),
            Protocol.TAnnouncementOffer => await HandleOfferAsync(session, env, ct),
            Protocol.TMediaToken => await HandleMediaTokenAsync(session, env, ct),
            Protocol.TAnnouncementEnd => await HandleAnnouncementEndAsync(session, env, ct),
            Protocol.TBellRing => HandleBellRing(env, ct),
            Protocol.TDeviceSettings => HandleDeviceSettings(env),
            Protocol.THistoryItem => HandleHistoryItem(env),
            Protocol.TUpdateOffer => HandleUpdateOffer(env, ct),
            _ => UnknownEvent(env),
        };

        if (processed && !string.IsNullOrEmpty(env.EventId))
        {
            Cursor.Advance(env.EventId);
            _processedEventIds.Add(env.EventId!);
        }
    }

    private bool UnknownEvent(ControlEnvelope env)
    {
        _log.LogWarning("ignoring unknown control message type '{Type}'", env.Type);
        return true; // consume it: an unknown type is not our failure to redeliver
    }

    private async Task<bool> HandleManifestAsync(ControlSession session, ControlEnvelope env, CancellationToken ct)
    {
        var manifest = PayloadSerializer.ToPayload<ScheduleManifestPayload>(env);
        if (manifest is null)
        {
            _log.LogWarning("schedule.manifest payload unparsable — ignoring");
            return true;
        }

        var verdict = Manifests.VerifyCandidate(manifest, _verifyKey);
        if (!verdict.Accepted)
        {
            // signature/revision failure: report and consume (never used, never retried spam)
            await session.SendAsync(ControlEnvelope.Create(Protocol.TManifestRejected,
                PayloadSerializer.Node(new ManifestRejectedPayload(verdict.Revision, verdict.Reason!))), ct);
            _log.LogWarning("manifest revision {Revision} rejected: {Reason}", verdict.Revision, verdict.Reason);
            return true;
        }

        // Atomic acceptance: download + sha256 verify + atomic cache swap BEFORE committing.
        SoundCacheResult cacheResult;
        try { cacheResult = await Cache.FetchAndSwapAsync(_http, manifest.Sounds, ct); }
        catch (OperationCanceledException) { throw; }
        if (!cacheResult.Success)
        {
            _log.LogWarning("manifest revision {Revision}: sound cache refresh failed ({Failure}) — not accepted yet, will re-deliver",
                manifest.Revision, cacheResult.Failure);
            return false; // cursor must NOT advance: server re-delivers on next resync
        }

        var now = Clock.NowMs();
        Manifests.Commit(manifest, now);
        Ledger.PruneTerminalBefore(manifest.HorizonStart);
        await session.SendAsync(ControlEnvelope.Create(Protocol.TScheduleAck,
            PayloadSerializer.Node(new ScheduleAckPayload(manifest.Revision, now, manifest.ExpiresAt))), ct);
        _log.LogInformation("accepted schedule manifest revision {Revision} ({Occurrences} occurrences, expires {ExpiresAt})",
            manifest.Revision, manifest.Occurrences.Count, manifest.ExpiresAt);

        WakeBellLoop();
        NotifyStateChanged();
        ManifestAccepted?.Invoke(manifest);
        return true;
    }

    private async Task<bool> HandleKeepAwakeSetAsync(ControlSession session, ControlEnvelope env, CancellationToken ct)
    {
        var payload = PayloadSerializer.ToPayload<KeepAwakeSetPayload>(env);
        if (payload is null || string.IsNullOrEmpty(payload.PolicyId)) return true;

        KeepAwakePolicies.Set(payload.PolicyId, payload.ExpiresAt, payload.DisplayOff);
        bool applied = await _keepAwake.RequestApplyAsync(payload.DisplayOff, ct);
        var now = Clock.NowMs();
        await session.SendAsync(ControlEnvelope.Create(Protocol.TKeepAwakeAck,
            PayloadSerializer.Node(new KeepAwakeAckPayload(payload.PolicyId, now, applied))), ct);
        _log.LogInformation("keepawake policy {PolicyId} set (expires_at={ExpiresAt}, display_off={DisplayOff}, applied={Applied})",
            payload.PolicyId, payload.ExpiresAt, payload.DisplayOff, applied);
        NotifyStateChanged();
        return true;
    }

    private async Task<bool> HandleKeepAwakeClearAsync(ControlSession session, ControlEnvelope env, CancellationToken ct)
    {
        var payload = PayloadSerializer.ToPayload<KeepAwakeClearPayload>(env);
        if (payload is null || string.IsNullOrEmpty(payload.PolicyId)) return true;

        KeepAwakePolicies.Clear(payload.PolicyId);
        if (!KeepAwakePolicies.Evaluate(Clock.NowMs()).Desired)
            await _keepAwake.RequestClearAsync(ct); // cleared on the same dedicated thread
        var now = Clock.NowMs();
        await session.SendAsync(ControlEnvelope.Create(Protocol.TKeepAwakeAck,
            PayloadSerializer.Node(new KeepAwakeAckPayload(payload.PolicyId, now, Applied: false))), ct);
        _log.LogInformation("keepawake policy {PolicyId} cleared", payload.PolicyId);
        NotifyStateChanged();
        return true;
    }
}

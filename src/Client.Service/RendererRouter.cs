using Microsoft.Extensions.Logging;
using SchoolBell.Client.Audio;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Service;

/// Chooses exactly one renderer per computer:
///   1. the tray companion in the active console session, when it holds the renderer slot;
///   2. otherwise the service's own session-0 renderer (signed out, booted with no user, or the
///      companion crashed/missing). Session-0 audio needs no interactive service and no change to
///      session isolation; it was verified on Windows 11 25H2 (TEST-RESULTS.md, Gate A).
/// An announcement stays on the renderer it started on, so a sign-in mid-announcement never
/// produces two copies of the same audio.
public sealed class RendererRouter : IBellPlayer, IAnnouncementPipeline, IRendererProvider, IDisposable
{
    private readonly PipeServerHost _pipe;
    private readonly AudioEngine _headless;
    private readonly VolumeRecoveryStore _recovery;
    private readonly ILogger _log;
    private readonly Dictionary<string, PipeServerHost.Session?> _annTargets = []; // null = headless
    private readonly Dictionary<string, PipeAnnStart> _annStarts = [];
    private readonly object _gate = new();

    public RendererRouter(PipeServerHost pipe, AudioEngine headless, VolumeRecoveryStore recovery, ILogger log)
    {
        _pipe = pipe; _headless = headless; _recovery = recovery; _log = log;
        _headless.Override.RecoveryChanged += rec => _recovery.Save(rec);
        _pipe.VolumeOverrideReported += rec => _recovery.Save(rec);
        _pipe.PendingVolumeRecovery = () => _recovery.Load();
        _pipe.RendererChanged += OnRendererChanged;
    }

    public string Owner => _pipe.Renderer is null ? "service" : "companion";

    /// True until a user has signed in at least once since boot. Verified on Windows 11 25H2:
    /// before the first sign-in, session-0 audio (ours and Windows' own SoundPlayer) is rendered
    /// as digital silence; after a sign-in + sign-out it is audible. So pre-login playback is
    /// attempted but never reported as completed (TEST-RESULTS.md, Gate A "no-user boot").
    public Func<bool> NoUserSinceBoot { get; set; } = () => false;

    /// Startup and renderer loss: a capture left behind by a crashed renderer is restored on the
    /// same endpoint (machine-wide master volume) by the service.
    public void RecoverPendingVolume()
    {
        if (_pipe.Renderer is not null) return; // the new companion gets volume.recover instead
        if (_recovery.Load() is { } rec && _headless.TryRecoverVolume(rec))
            _recovery.Save(null);
    }

    private void OnRendererChanged()
    {
        _log.LogInformation("active renderer: {Owner}", Owner);
        if (_pipe.Renderer is not null) return;
        RecoverPendingVolume();
        // The companion vanished mid-announcement (crash, sign-out): the service renderer plays
        // the rest of the stream. Audio already sent to the dead companion is lost (a short gap).
        List<PipeAnnStart> orphaned;
        lock (_gate)
        {
            orphaned = _annTargets.Where(kv => kv.Value is not null && _annStarts.ContainsKey(kv.Key))
                .Select(kv => _annStarts[kv.Key]).ToList();
            foreach (var a in orphaned) _annTargets[a.AnnouncementId] = null;
        }
        foreach (var a in orphaned)
        {
            var (ok, reason) = _headless.StartAnnouncement(a with { Chime = false });
            _log.LogWarning("announcement {Id}: companion lost mid-stream; service renderer continues ({Result})",
                a.AnnouncementId, ok ? "ok" : reason);
        }
    }

    // ---------------------------------------------------------------- IRendererProvider

    public RendererInfo GetRenderer()
    {
        var r = _pipe.Renderer;
        if (r?.LastReport is { } rep)
            return new RendererInfo(rep.Renderer, rep.RendererReason, rep.OutputDevice, rep.OutputPresent, false);
        if (r is not null) return new RendererInfo("no_renderer", "companion_starting", null, false, false);
        var s = _headless.GetStatus();
        if (s.Renderer == "ready" && NoUserSinceBoot())
            return new RendererInfo("no_renderer", "no_user_since_boot", s.OutputDevice, s.OutputPresent, false);
        return new RendererInfo(s.Renderer, s.Reason ?? "service_renderer", s.OutputDevice, s.OutputPresent, false);
    }

    public VolumeSnapshot GetVolumes()
    {
        if (_pipe.Renderer?.LastReport is { } rep)
            return new VolumeSnapshot(rep.MasterMuted, rep.MasterVolume, rep.SessionMuted, rep.SessionVolume);
        var s = _headless.GetStatus();
        return new VolumeSnapshot(s.MasterMuted, s.MasterVolume, s.SessionMuted, s.SessionVolume);
    }

    public string CompanionState => _pipe.CompanionState;
    public IReadOnlyList<string> AvailableOutputs => _headless.ListOutputs();
    public string RendererOwner => Owner;

    // ---------------------------------------------------------------- IBellPlayer

    public async Task<BellPlayOutcome> PlayAsync(PipeBellPlay play, CancellationToken ct)
    {
        var companion = _pipe.Renderer;
        if (companion is not null)
        {
            var outcome = await _pipe.PlayOnCompanionAsync(companion, play, ct);
            if (outcome.Played || outcome.Reason is not ("renderer_lost" or "renderer_timeout")) return outcome;
            // companion vanished before playing: the bell is still due, use the service renderer
            _log.LogWarning("companion did not play {Id} ({Reason}); falling back to the service renderer", play.OccurrenceId, outcome.Reason);
        }
        var played = await _headless.PlayBellAsync(play, ct);
        if (played.Played && NoUserSinceBoot())
            return new BellPlayOutcome(true, "pre_login_audio_unverified", ReceiptState.Unknown);
        return played;
    }

    // ---------------------------------------------------------------- IAnnouncementPipeline

    public Task StartAsync(PipeAnnStart start, CancellationToken ct)
    {
        var companion = _pipe.Renderer;
        lock (_gate) _annStarts[start.AnnouncementId] = start;
        if (companion is not null && _pipe.SendAnnStart(companion, start))
        {
            lock (_gate) _annTargets[start.AnnouncementId] = companion;
            return Task.CompletedTask;
        }
        var (ok, reason) = _headless.StartAnnouncement(start);
        if (!ok) { lock (_gate) _annStarts.Remove(start.AnnouncementId); throw new InvalidOperationException($"no renderer: {reason}"); }
        lock (_gate) _annTargets[start.AnnouncementId] = null;
        return Task.CompletedTask;
    }

    public Task FramesAsync(string announcementId, IReadOnlyList<MediaFrame> frames, CancellationToken ct)
    {
        PipeServerHost.Session? target;
        bool known;
        lock (_gate) known = _annTargets.TryGetValue(announcementId, out target);
        if (!known) return Task.CompletedTask;
        if (target is not null) _pipe.SendAnnFrames(target, announcementId, frames);
        else _headless.PushFrames(announcementId, frames.Select(f => f.Pcm.ToArray()));
        return Task.CompletedTask;
    }

    public Task EndAsync(string announcementId, string endedBy, CancellationToken ct)
    {
        PipeServerHost.Session? target;
        bool known;
        lock (_gate) { known = _annTargets.Remove(announcementId, out target); _annStarts.Remove(announcementId); }
        if (!known) return Task.CompletedTask;
        if (target is not null) _pipe.SendAnnEnd(target, announcementId, endedBy);
        else _headless.EndAnnouncement(announcementId);
        return Task.CompletedTask;
    }

    public void Dispose() => _headless.Dispose();
}

/// Durable copy of the last volume capture (ProgramData, SYSTEM/Administrators only).
public sealed class VolumeRecoveryStore
{
    private readonly JsonStore<VolumeRecoveryRecord> _store;
    private readonly object _gate = new();
    public VolumeRecoveryStore(string dataDir) => _store = new JsonStore<VolumeRecoveryRecord>(Path.Combine(dataDir, "volume-recovery.json"));
    public VolumeRecoveryRecord? Load() { lock (_gate) return _store.Load(); }
    public void Save(VolumeRecoveryRecord? rec)
    {
        lock (_gate)
        {
            if (rec is null) { try { File.Delete(_store.Path); } catch (IOException) { } }
            else _store.Save(rec);
        }
    }
}

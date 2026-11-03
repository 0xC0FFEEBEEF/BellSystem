using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SchoolBell.Client.Core;

namespace SchoolBell.Client.Audio;

public sealed record AudioSettings(string? OutputDevice, int AnnouncementVolume, int VolumeCap);

public sealed record AudioStatus(
    string Renderer, string? Reason, string? OutputDevice, bool OutputPresent,
    bool MasterMuted, int MasterVolume, bool SessionMuted, int SessionVolume, bool Playing);

/// WASAPI shared-mode renderer used by the tray companion (signed-in console user) and by the
/// service's signed-out fallback. One mixer per process; the output stream is opened only while
/// something is playing and closed after 2 s idle, so an idle SchoolBell never holds an audio
/// stream open (an open stream would block idle sleep and show in the volume mixer).
/// Every public method is safe to call from any thread except a WinForms STA UI thread.
public sealed class AudioEngine : IDisposable
{
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceWatcher _watcher;
    private readonly Timer _idle;
    private readonly Timer _poll;
    private readonly Dictionary<string, LivePlayback> _live = [];
    private AudioSettings _settings = new(null, 80, 100);
    private WasapiOut? _out;
    private MixingSampleProvider? _mixer;
    private string? _outDeviceId;
    private int _mixRate, _mixChannels;
    private long _idleSinceMs = -1;
    private readonly List<TrackedSource> _inputs = [];
    private AudioStatus? _lastStatus;

    public VolumeOverrideManager Override { get; }
    public event Action? StatusChanged;
    public event Action<string>? AnnouncementFinished;
    public int JitterStartMs { get; set; } = 300;
    public int JitterMaxMs { get; set; } = 2000;

    private sealed class LivePlayback
    {
        public required PcmJitterBuffer Jitter { get; init; }
        public required TrackedSource Source { get; init; }
        public required long StartedAtMs { get; init; }
        public required int MaxDurationS { get; init; }
        public bool OverrideHeld { get; set; }
    }

    public AudioEngine(ILogger log, string owner)
    {
        _log = log;
        Override = new VolumeOverrideManager(log, owner);
        _watcher = new DeviceWatcher(this);
        try { _enumerator.RegisterEndpointNotificationCallback(_watcher); }
        catch (Exception e) { _log.LogWarning("audio device notifications unavailable: {Error}", e.Message); }
        _idle = new Timer(_ => IdleCheck(), null, 1000, 1000);
        _poll = new Timer(_ => PollStatus(), null, 500, 2000);
    }

    public void ApplySettings(AudioSettings settings)
    {
        lock (_gate) _settings = settings;
        _log.LogInformation("audio settings: output '{Output}', announcement volume {Vol}% (cap {Cap}%)",
            settings.OutputDevice ?? "(Windows default)", settings.AnnouncementVolume, settings.VolumeCap);
        PollStatus();
    }

    /// Resolve the configured speaker. Null configuration = the Windows default output (documented);
    /// a configured name that is not present is a failure, never a silent fallback.
    private MMDevice? ResolveDevice(out string? reason)
    {
        reason = null;
        var want = _settings.OutputDevice;
        try
        {
            if (string.IsNullOrWhiteSpace(want))
            {
                if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) { reason = "missing_output"; return null; }
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            var active = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var dev = active.FirstOrDefault(d => d.ID == want)
                      ?? active.FirstOrDefault(d => string.Equals(d.FriendlyName, want, StringComparison.OrdinalIgnoreCase))
                      ?? active.FirstOrDefault(d => d.FriendlyName.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (dev is null) reason = "missing_output";
            return dev;
        }
        catch (COMException e)
        {
            _log.LogWarning("audio endpoint lookup failed: {Error}", e.Message);
            reason = "missing_output";
            return null;
        }
    }

    public List<string> ListOutputs()
    {
        try { return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.FriendlyName).ToList(); }
        catch (COMException) { return []; }
    }

    public AudioStatus GetStatus()
    {
        lock (_gate)
        {
            bool playing = _inputs.Count > 0;
            var dev = ResolveDevice(out var reason);
            if (dev is null)
                return new("error", reason ?? "missing_output", _settings.OutputDevice, false, false, 0, false, 0, playing);
            try
            {
                var ep = dev.AudioEndpointVolume;
                var sv = dev.AudioSessionManager.SimpleAudioVolume;
                return new("ready", null, dev.FriendlyName, true, ep.Mute, (int)Math.Round(ep.MasterVolumeLevelScalar * 100),
                    sv.Mute, (int)Math.Round(sv.Volume * 100), playing);
            }
            catch (Exception e)
            {
                _log.LogWarning("reading volume of '{Device}' failed: {Error}", dev.FriendlyName, e.Message);
                return new("error", "volume_unreadable", dev.FriendlyName, true, false, 0, false, 0, playing);
            }
        }
    }

    private void PollStatus()
    {
        AudioStatus s;
        try { s = GetStatus(); } catch (Exception) { return; }
        if (s == _lastStatus) return;
        _lastStatus = s;
        StatusChanged?.Invoke();
    }

    // ------------------------------------------------------------------ output lifecycle

    private (bool Ok, string? Reason, MMDevice? Device) EnsureOutputLocked()
    {
        var dev = ResolveDevice(out var reason);
        if (dev is null) return (false, reason ?? "missing_output", null);
        if (_out is not null && _outDeviceId == dev.ID) return (true, null, dev);
        if (_out is not null) TeardownLocked("output device changed");
        try
        {
            int rate, channels;
            using (var ac = dev.AudioClient)
            {
                rate = ac.MixFormat.SampleRate;
                channels = ac.MixFormat.Channels;
            }
            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)) { ReadFully = true };
            var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
            output.Init(mixer);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    _log.LogWarning("audio output stopped with an error: {Error}", e.Exception.Message);
                    lock (_gate) if (ReferenceEquals(_out, output)) TeardownLocked("output error");
                }
            };
            output.Play();
            _out = output; _mixer = mixer; _outDeviceId = dev.ID; _mixRate = rate; _mixChannels = channels;
            _idleSinceMs = -1;
            _log.LogInformation("audio output opened on '{Device}' ({Rate} Hz, {Ch} ch, shared mode)", dev.FriendlyName, rate, channels);
            return (true, null, dev);
        }
        catch (Exception e)
        {
            _log.LogWarning("opening audio output on '{Device}' failed: {Error}", dev.FriendlyName, e.Message);
            TeardownLocked("open failed");
            return (false, "renderer", null);
        }
    }

    private void TeardownLocked(string why)
    {
        var o = _out;
        _out = null; _mixer = null; _outDeviceId = null;
        foreach (var t in _inputs) t.Abandon();
        _inputs.Clear();
        if (o is null) return;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
        _log.LogInformation("audio output closed ({Why})", why);
    }

    private void IdleCheck()
    {
        lock (_gate)
        {
            _inputs.RemoveAll(t => t.Completion.IsCompleted);
            if (_out is null) return;
            if (_inputs.Count > 0) { _idleSinceMs = -1; return; }
            var now = Environment.TickCount64;
            if (_idleSinceMs < 0) { _idleSinceMs = now; return; }
            if (now - _idleSinceMs >= 2000) TeardownLocked("idle");
        }
        // safety net: a live stream that outlived its max duration is ended here
        List<string> overdue;
        lock (_gate)
            overdue = _live.Where(kv => Environment.TickCount64 - kv.Value.StartedAtMs > (kv.Value.MaxDurationS + 15) * 1000L)
                .Select(kv => kv.Key).ToList();
        foreach (var id in overdue)
        {
            _log.LogWarning("announcement {Id} exceeded its max duration in the renderer — stopping", id);
            EndAnnouncement(id, force: true);
        }
    }

    private TrackedSource AddInputLocked(ISampleProvider mono)
    {
        ISampleProvider sp = mono;
        if (sp.WaveFormat.SampleRate != _mixRate) sp = new WdlResamplingSampleProvider(sp, _mixRate);
        var tracked = new TrackedSource(new MonoToChannels(sp, _mixChannels));
        _inputs.Add(tracked);
        _mixer!.AddMixerInput(tracked);
        _idleSinceMs = -1;
        return tracked;
    }

    // ------------------------------------------------------------------ bells

    public async Task<BellPlayOutcome> PlayBellAsync(PipeBellPlay play, CancellationToken ct)
    {
        if (!File.Exists(play.WavPath)) return new(false, "sound_missing");
        TrackedSource tracked;
        bool overrideHeld = false;
        lock (_gate)
        {
            var (ok, reason, dev) = EnsureOutputLocked();
            if (!ok) return new(false, reason == "missing_output" ? "output_missing" : reason);
            float[] samples;
            try { samples = SoundLoader.LoadMono(play.WavPath, _mixRate, play.DurationS, play.FadeMs); }
            catch (Exception e)
            {
                _log.LogWarning("bell sound '{Path}' could not be decoded: {Error}", play.WavPath, e.Message);
                return new(false, "sound_unreadable");
            }
            if (play.VolumePolicy == "announcement")
            {
                try { Override.Acquire(dev!, play.PolicyVolume); overrideHeld = true; }
                catch (Exception e) { _log.LogWarning("volume override failed: {Error}", e.Message); }
            }
            tracked = AddInputLocked(new MonoBufferSource(samples, _mixRate));
        }
        StatusChanged?.Invoke();
        try
        {
            var budget = TimeSpan.FromSeconds(Math.Max(5, play.DurationS + 10));
            var finished = await Task.WhenAny(tracked.Completion, Task.Delay(budget, ct));
            if (finished != tracked.Completion) { tracked.Cancel(); return new(false, "renderer_timeout"); }
            return tracked.SamplesRead > 0 ? new(true, null) : new(false, "renderer");
        }
        catch (OperationCanceledException) { tracked.Cancel(); throw; }
        finally
        {
            if (overrideHeld) Override.Release();
            StatusChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ announcements

    public (bool Ok, string? Reason) StartAnnouncement(PipeAnnStart start)
    {
        lock (_gate)
        {
            if (_live.ContainsKey(start.AnnouncementId)) return (true, null);
            var (ok, reason, dev) = EnsureOutputLocked();
            if (!ok) return (false, reason);
            bool held = false;
            try { Override.Acquire(dev!, start.PolicyVolume); held = true; }
            catch (Exception e) { _log.LogWarning("volume override failed: {Error}", e.Message); }
            long leadIn = 0;
            if (start.Chime)
            {
                var chime = SoundLoader.Chime(_mixRate);
                AddInputLocked(new MonoBufferSource(chime, _mixRate));
                leadIn = (long)chime.Length * 16_000 / _mixRate + 16_000 / 10;   // chime + 100 ms gap, in 16 kHz samples
            }
            var jitter = new PcmJitterBuffer(16_000, JitterStartMs, JitterMaxMs);
            var tracked = AddInputLocked(new JitterSource(jitter, leadIn));
            var pb = new LivePlayback
            {
                Jitter = jitter, Source = tracked, StartedAtMs = Environment.TickCount64,
                MaxDurationS = Math.Max(10, start.MaxDurationS), OverrideHeld = held,
            };
            _live[start.AnnouncementId] = pb;
            var id = start.AnnouncementId;
            tracked.Completion.ContinueWith(_ => OnLiveFinished(id), TaskScheduler.Default);
        }
        _log.LogInformation("announcement {Id} ({Kind}) rendering started (chime: {Chime})", start.AnnouncementId, start.Kind, start.Chime);
        StatusChanged?.Invoke();
        return (true, null);
    }

    public void PushFrames(string announcementId, IEnumerable<byte[]> pcmFrames)
    {
        LivePlayback? pb;
        lock (_gate) _live.TryGetValue(announcementId, out pb);
        if (pb is null) return;
        foreach (var f in pcmFrames) pb.Jitter.Write(f);
    }

    /// Ends the stream: queued valid audio drains (bounded by the jitter maximum), then the
    /// volume is restored. `force` stops immediately.
    public void EndAnnouncement(string announcementId, bool force = false)
    {
        LivePlayback? pb;
        lock (_gate) _live.TryGetValue(announcementId, out pb);
        if (pb is null) return;
        pb.Jitter.End();
        if (force) pb.Source.Cancel();
        // the drain is at most JitterMaxMs; if the output stalled, force completion
        _ = Task.Delay(JitterMaxMs + 3000).ContinueWith(_ => { pb.Source.Cancel(); pb.Source.Abandon(); });
    }

    public (long Underruns, long Dropped, int BufferedMs)? JitterStats(string announcementId)
    {
        lock (_gate)
            return _live.TryGetValue(announcementId, out var pb)
                ? (pb.Jitter.Underruns, pb.Jitter.DroppedSamples, pb.Jitter.BufferedMs) : null;
    }

    private void OnLiveFinished(string id)
    {
        LivePlayback? pb;
        lock (_gate)
        {
            if (!_live.Remove(id, out pb)) return;
        }
        if (pb.OverrideHeld) Override.Release();
        _log.LogInformation("announcement {Id} rendering finished (underruns {U}, dropped {D} samples)",
            id, pb.Jitter.Underruns, pb.Jitter.DroppedSamples);
        AnnouncementFinished?.Invoke(id);
        StatusChanged?.Invoke();
    }

    public bool TryRecoverVolume(VolumeRecoveryRecord rec) => Override.TryRecover(rec, _enumerator);

    public void Dispose()
    {
        _idle.Dispose();
        _poll.Dispose();
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        lock (_gate)
        {
            foreach (var id in _live.Keys.ToList()) _live[id].Source.Cancel();
            TeardownLocked("shutdown");
        }
        Override.Dispose();
    }

    /// Device add/remove/default changes: re-evaluate status promptly (and tear down an output
    /// whose device vanished so the next playback reopens on the right speaker).
    private sealed class DeviceWatcher(AudioEngine engine) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed(deviceId);
        public void OnDeviceAdded(string pwstrDeviceId) => Changed(pwstrDeviceId);
        public void OnDeviceRemoved(string deviceId) => Changed(deviceId);
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render) Changed(defaultDeviceId);
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        private void Changed(string id) => ThreadPool.QueueUserWorkItem(state =>
        {
            try
            {
                lock (engine._gate)
                    if (engine._out is not null && engine._outDeviceId == id && engine.ResolveDevice(out _)?.ID != id)
                        engine.TeardownLocked("device removed or replaced");
                if (engine.Override.Pending is { } pending && pending.DeviceId == id)
                    engine.TryRecoverVolume(pending);
                engine.PollStatus();
            }
            catch (Exception) { }
        });
    }
}

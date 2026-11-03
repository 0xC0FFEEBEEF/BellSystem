using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using SchoolBell.Client.Core;

namespace SchoolBell.Client.Audio;

/// Temporary announcement volume (plan §9). The first holder captures the endpoint (master) and
/// this process's audio-session mute/volume, persists the capture (RecoveryChanged) BEFORE
/// changing anything, then unmutes both and applies the target level. Overlapping holders share
/// one override; the last release restores the captured values to the same endpoint only.
///
/// While held, a user who mutes or turns the volume down is overridden again, at most once per
/// two seconds (bounded). On restore the pre-announcement values come back: changes made during
/// the announcement are not kept. Documented in OPERATIONS.md.
public sealed class VolumeOverrideManager : IDisposable
{
    private readonly object _gate = new();
    private readonly ILogger _log;
    private readonly string _owner;
    private readonly Timer _reapply;
    private int _refs;
    private MMDevice? _device;
    private VolumeRecoveryRecord? _captured;
    private float _target;
    private long _lastReapplyMs;

    public event Action<VolumeRecoveryRecord?>? RecoveryChanged;
    public int Reapplied { get; private set; }

    public VolumeOverrideManager(ILogger log, string owner)
    {
        _log = log;
        _owner = owner;
        _reapply = new Timer(_ => Reapply(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Active { get { lock (_gate) return _refs > 0; } }

    public void Acquire(MMDevice device, int targetPercent)
    {
        lock (_gate)
        {
            float target = Math.Clamp(targetPercent, 0, 100) / 100f;
            if (_refs == 0)
            {
                var ep = device.AudioEndpointVolume;
                var sv = device.AudioSessionManager.SimpleAudioVolume;
                _captured = new VolumeRecoveryRecord(device.ID, device.FriendlyName, ep.Mute,
                    ep.MasterVolumeLevelScalar, sv.Mute, sv.Volume, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _owner);
                RecoveryChanged?.Invoke(_captured);          // persisted before any change
                _device = device;
                _target = target;
                Apply(device, target);
                _log.LogInformation("volume override on '{Device}': master {Prev}%{PrevMute} -> {Target}%, session {SPrev}%{SMute} -> 100%",
                    device.FriendlyName, (int)Math.Round(_captured.MasterScalar * 100), _captured.MasterMuted ? " (muted)" : "",
                    targetPercent, (int)Math.Round(_captured.SessionVolume * 100), _captured.SessionMuted ? " (muted)" : "");
                _reapply.Change(1000, 1000);
            }
            else if (target > _target && _device is not null)
            {
                _target = target;
                Apply(_device, target);
            }
            _refs++;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_refs == 0) return;
            if (--_refs > 0) return;
            _reapply.Change(Timeout.Infinite, Timeout.Infinite);
            var rec = _captured;
            var dev = _device;
            _device = null;
            if (rec is null || dev is null) return;
            bool restored = false;
            try
            {
                if (dev.State == DeviceState.Active && dev.ID == rec.DeviceId)
                {
                    Restore(dev, rec);
                    restored = true;
                }
            }
            catch (Exception e) { _log.LogWarning("volume restore failed: {Error}", e.Message); }
            if (restored)
            {
                _captured = null;
                RecoveryChanged?.Invoke(null);
                _log.LogInformation("volume restored on '{Device}' to master {Vol}%{Mute}", rec.DeviceName,
                    (int)Math.Round(rec.MasterScalar * 100), rec.MasterMuted ? " (muted)" : "");
            }
            else
            {
                // speaker gone: keep the record pending; it is restored when that same endpoint returns
                _log.LogWarning("volume restore deferred: '{Device}' is not present", rec.DeviceName);
            }
        }
    }

    /// Crash recovery: restore a persisted capture if (and only if) its endpoint is present.
    public bool TryRecover(VolumeRecoveryRecord rec, MMDeviceEnumerator enumerator)
    {
        lock (_gate)
        {
            if (_refs > 0) return false;
            MMDevice? dev = null;
            try { dev = enumerator.GetDevice(rec.DeviceId); } catch (Exception) { }
            if (dev is null || dev.State != DeviceState.Active) return false;
            try { Restore(dev, rec); }
            catch (Exception e) { _log.LogWarning("volume recovery failed: {Error}", e.Message); return false; }
            _captured = null;
            RecoveryChanged?.Invoke(null);
            _log.LogWarning("restored volume left overridden by an interrupted announcement on '{Device}' (captured by {Owner})",
                rec.DeviceName, rec.Owner);
            return true;
        }
    }

    public VolumeRecoveryRecord? Pending { get { lock (_gate) return _refs == 0 ? _captured : null; } }

    private void Reapply()
    {
        lock (_gate)
        {
            if (_refs == 0 || _device is null) return;
            try
            {
                var ep = _device.AudioEndpointVolume;
                var sv = _device.AudioSessionManager.SimpleAudioVolume;
                bool drift = ep.Mute || Math.Abs(ep.MasterVolumeLevelScalar - _target) > 0.02f || sv.Mute || sv.Volume < 0.98f;
                var now = Environment.TickCount64;
                if (!drift || now - _lastReapplyMs < 2000) return;
                _lastReapplyMs = now;
                Apply(_device, _target);
                Reapplied++;
                _log.LogInformation("volume changed during an announcement — announcement level reapplied");
            }
            catch (Exception e) { _log.LogDebug("reapply check failed: {Error}", e.Message); }
        }
    }

    private static void Apply(MMDevice dev, float target)
    {
        var ep = dev.AudioEndpointVolume;
        if (ep.Mute) ep.Mute = false;
        ep.MasterVolumeLevelScalar = target;
        var sv = dev.AudioSessionManager.SimpleAudioVolume;
        if (sv.Mute) sv.Mute = false;
        sv.Volume = 1.0f;
    }

    private static void Restore(MMDevice dev, VolumeRecoveryRecord rec)
    {
        var ep = dev.AudioEndpointVolume;
        ep.MasterVolumeLevelScalar = Math.Clamp(rec.MasterScalar, 0f, 1f);
        ep.Mute = rec.MasterMuted;
        var sv = dev.AudioSessionManager.SimpleAudioVolume;
        sv.Volume = Math.Clamp(rec.SessionVolume, 0f, 1f);
        sv.Mute = rec.SessionMuted;
    }

    public void Dispose() => _reapply.Dispose();
}

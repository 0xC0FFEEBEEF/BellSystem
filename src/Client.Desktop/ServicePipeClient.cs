using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Audio;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Desktop;

/// Companion side of \\.\pipe\schoolbell-v1. Asks for the renderer slot; renders audio only when
/// the service grants it (console session). Accepts only the documented service messages and
/// never runs commands or opens paths other than sound files inside the service's cache folder.
internal sealed class ServicePipeClient : IDisposable
{
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeClientStream? _pipe;
    private AudioEngine? _engine;
    public int BellNotificationSeconds { get; private set; } = 12;
    private AudioSettings _settings = new(null, 80, 100);
    private string _cacheDir = "";
    private readonly string _localRecoveryPath;

    public event Action? Changed;
    public event Action<PipeBellPlay>? BellPlaying;
    public event Action<PipeAnnStart>? AnnouncementStarted;
    public event Action<PipeAnnEnd>? AnnouncementEnded;
    public event Action<PipeAnnTranscript>? TranscriptUpdated;
    public event Action<TranscriptNotificationPayload>? TranscriptNotificationRequested;
    public bool PipeConnected { get; private set; }
    public string Slot { get; private set; } = "none";
    public PipeServiceState? ServiceState { get; private set; }
    public PipeManifestState? Manifest { get; private set; }
    public PipeKeepAwakeState? KeepAwake { get; private set; }
    public List<HistoryItemPayload> History { get; private set; } = [];
    public AudioStatus? Audio => _engine?.GetStatus();
    public string? LastEvent { get; private set; }

    public ServicePipeClient(ILogger log)
    {
        _log = log;
        _localRecoveryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SchoolBell", "volume-recovery.json");
    }

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var delay = 1000;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, _stop.Token);
                _pipe = pipe;
                PipeConnected = true;
                delay = 1000;
                Changed?.Invoke();
                await SendAsync(PipeProtocol.THello, new PipeHello("renderer", Version()));
                using var hb = new System.Threading.Timer(_ => _ = SendAsync(PipeProtocol.THeartbeat, new { }), null, 0, PipeProtocol.CompanionHeartbeatMs);
                await ReadLoopAsync(pipe);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.LogInformation("service pipe unavailable: {Error}", e.Message);
            }
            OnDisconnected();
            try { await Task.Delay(delay, _stop.Token); } catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 15_000);
        }
    }

    private void OnDisconnected()
    {
        var wasConnected = PipeConnected;
        PipeConnected = false;
        _pipe = null;
        ServiceState = null;
        DropRenderer("service pipe closed");
        if (wasConnected) Changed?.Invoke();
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        while (!_stop.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(_stop.Token);
            if (line is null) return;
            var msg = PipeProtocol.ParseLine(line);
            if (msg is null) continue;
            try { Dispatch(msg); }
            catch (Exception e) { _log.LogWarning("handling '{Type}' failed: {Error}", msg.Type, e.Message); }
        }
    }

    private void Dispatch(PipeMessage msg)
    {
        switch (msg.Type)
        {
            case PipeProtocol.THelloOk:
                var ok = msg.PayloadAs<PipeHelloOk>();
                if (ok is null) return;
                _cacheDir = ok.CacheDir;
                Slot = ok.SlotGranted;
                if (Slot == "renderer") EnsureRenderer(); else DropRenderer("renderer slot not granted (not the console session)");
                Changed?.Invoke();
                break;

            case PipeTypes.Settings:
                var st = msg.PayloadAs<PipeSettings>();
                if (st is null) return;
                BellNotificationSeconds = Math.Clamp(st.BellNotificationSeconds, 0, 300);
                _settings = new AudioSettings(st.OutputDevice, st.AnnouncementVolume, st.VolumeCap);
                _engine?.ApplySettings(_settings);
                break;

            case PipeProtocol.TBellPlay:
                var play = msg.PayloadAs<PipeBellPlay>();
                if (play is null) return;
                BellPlaying?.Invoke(play);
                _ = Task.Run(() => PlayBellAsync(play));
                break;

            case PipeProtocol.TAnnStart:
                var start = msg.PayloadAs<PipeAnnStart>();
                if (start is null) return;
                AnnouncementStarted?.Invoke(start);
                if (_engine is null) return;
                var (started, reason) = _engine.StartAnnouncement(start);
                LastEvent = started ? $"Announcement: {start.SourceLabel}" : $"Announcement could not play ({reason})";
                Changed?.Invoke();
                break;

            case PipeProtocol.TAnnFrames:
                var frames = msg.PayloadAs<PipeAnnFrames>();
                if (frames is null || _engine is null) return;
                _engine.PushFrames(frames.AnnouncementId, frames.Frames.Select(f => Convert.FromBase64String(f.PcmB64)));
                break;

            case PipeProtocol.TAnnEnd:
                var end = msg.PayloadAs<PipeAnnEnd>();
                if (end is null) return;
                _engine?.EndAnnouncement(end.AnnouncementId);
                AnnouncementEnded?.Invoke(end);
                break;

            case PipeProtocol.TAnnTranscript:
                var transcript = msg.PayloadAs<PipeAnnTranscript>();
                if (transcript is null) return;
                TranscriptUpdated?.Invoke(transcript);
                break;

            case PipeProtocol.TTranscriptNotify:
                var notice = msg.PayloadAs<TranscriptNotificationPayload>();
                if (notice?.Item is { Schoolwide: true, TranscriptStatus: "done" })
                    TranscriptNotificationRequested?.Invoke(notice);
                break;

            case PipeProtocol.TManifestState:
                Manifest = msg.PayloadAs<PipeManifestState>();
                Changed?.Invoke();
                break;

            case PipeProtocol.TKeepAwakeState:
                KeepAwake = msg.PayloadAs<PipeKeepAwakeState>();
                Changed?.Invoke();
                break;

            case PipeTypes.HistoryList:
                History = msg.PayloadAs<PipeHistoryList>()?.Items ?? [];
                Changed?.Invoke();
                break;

            case PipeTypes.ServiceState:
                ServiceState = msg.PayloadAs<PipeServiceState>();
                Changed?.Invoke();
                break;

            case PipeTypes.VolumeRecover:
                var rec = msg.PayloadAs<PipeVolumeRecover>()?.Record;
                if (rec is not null && _engine is not null && _engine.TryRecoverVolume(rec))
                    _ = SendAsync(PipeTypes.VolumeOverride, new PipeVolumeOverride(null));
                break;
        }
    }

    private async Task PlayBellAsync(PipeBellPlay play)
    {
        BellPlayOutcome outcome;
        if (_engine is null) outcome = new(false, "no_renderer");
        else if (!IsInsideCache(play.WavPath)) outcome = new(false, "invalid_path");
        else
        {
            try { outcome = await _engine.PlayBellAsync(play, _stop.Token); }
            catch (Exception e) { _log.LogWarning("bell playback failed: {Error}", e.Message); outcome = new(false, "renderer"); }
        }
        LastEvent = outcome.Played ? $"Bell: {play.BellName} at {DateTime.Now:t}" : $"Bell '{play.BellName}' failed ({outcome.Reason})";
        Changed?.Invoke();
        await SendAsync(PipeProtocol.TBellResult, new PipeBellResult(play.OccurrenceId, outcome.Played ? "played" : "failed", outcome.Reason));
    }

    /// Only sound files inside the service's cache folder are ever opened.
    private bool IsInsideCache(string path)
    {
        if (string.IsNullOrEmpty(_cacheDir)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Path.Combine(_cacheDir, ".."));   // cache\current and cache\adhoc
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   && full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private void EnsureRenderer()
    {
        if (_engine is not null) return;
        // Core Audio objects are created off the STA UI thread.
        var engine = Task.Run(() => new AudioEngine(_log, "companion")).GetAwaiter().GetResult();
        engine.ApplySettings(_settings);
        engine.StatusChanged += () => { _ = SendStatusAsync(); Changed?.Invoke(); };
        engine.Override.RecoveryChanged += rec =>
        {
            SaveLocalRecovery(rec);
            _ = SendAsync(PipeTypes.VolumeOverride, new PipeVolumeOverride(rec));
        };
        _engine = engine;
        // Crash recovery comes only from the service's durable record (volume.recover), never
        // from a stale local copy: the service may already have restored and taken over.
        SaveLocalRecovery(null);
        _log.LogInformation("renderer slot granted: this companion plays bells and announcements");
        _ = SendStatusAsync();
    }

    private void DropRenderer(string why)
    {
        var e = _engine;
        if (e is null) return;
        _engine = null;
        _log.LogInformation("renderer released: {Why}", why);
        try { e.Dispose(); } catch { }
        Changed?.Invoke();
    }

    private async Task SendStatusAsync()
    {
        var a = _engine?.GetStatus();
        var report = a is null
            ? new PipeStatusReport("no_renderer", Slot == "renderer" ? "starting" : "renderer_slot_taken", null, false, false, 0, false, 0, "running")
            : new PipeStatusReport(a.Renderer, a.Reason, a.OutputDevice, a.OutputPresent, a.MasterMuted, a.MasterVolume,
                a.SessionMuted, a.SessionVolume, "running");
        await SendAsync(PipeProtocol.TStatusReport, report);
    }

    private async Task SendAsync(string type, object payload)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return;
        var bytes = Encoding.UTF8.GetBytes(PipeProtocol.SerializeLine(type, payload) + "\n");
        await _writeLock.WaitAsync();
        try { await pipe.WriteAsync(bytes); await pipe.FlushAsync(); }
        catch (Exception) { /* read loop notices the disconnect */ }
        finally { _writeLock.Release(); }
    }

    public void RequestHistory() => _ = SendAsync(PipeTypes.HistoryGet, new { });

    private void SaveLocalRecovery(VolumeRecoveryRecord? rec)
    {
        try
        {
            if (rec is null) { File.Delete(_localRecoveryPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_localRecoveryPath)!);
            File.WriteAllText(_localRecoveryPath, JsonSerializer.Serialize(rec, PipeProtocol.Json));
        }
        catch (IOException) { }
    }

    private VolumeRecoveryRecord? LoadLocalRecovery()
    {
        try
        {
            return File.Exists(_localRecoveryPath)
                ? JsonSerializer.Deserialize<VolumeRecoveryRecord>(File.ReadAllText(_localRecoveryPath), PipeProtocol.Json)
                : null;
        }
        catch (Exception) { return null; }
    }

    public static string Version()
    {
        var v = typeof(ServicePipeClient).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }

    public void Dispose()
    {
        _stop.Cancel();
        DropRenderer("companion exiting");
        try { _pipe?.Dispose(); } catch { }
    }
}

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Service;

/// Named pipe server \\.\pipe\schoolbell-v1 (companion <-> service, JSON lines).
///
/// Access: the DACL grants SYSTEM, Administrators and INTERACTIVE users, and explicitly denies
/// NETWORK logons, so remote clients are refused by the OS. Each connection is then authorized
/// by the Windows session of the connecting process (GetNamedPipeClientProcessId +
/// ProcessIdToSessionId): only a companion in the active console session can hold the renderer
/// slot. Companions in other sessions (RDP, fast-user-switched) are status-only, so one computer
/// never renders twice. The companion can send only the documented message types.
public sealed class PipeServerHost : IDisposable
{
    public sealed class Session
    {
        public required NamedPipeServerStream Pipe { get; init; }
        public required SemaphoreSlim WriteLock { get; init; }
        public required uint WindowsSession { get; init; }
        public required int ProcessId { get; init; }
        public bool IsRenderer { get; set; }
        public bool WantsRenderer { get; set; }
        public long LastSeenMs { get; set; }
        public string CompanionState { get; set; } = "running"; // running | hidden
        public PipeStatusReport? LastReport { get; set; }
    }

    private readonly ClientConfig _cfg;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<BellPlayOutcome>> _pendingBells = [];
    private readonly List<Session> _sessions = [];
    private Session? _renderer;
    private DeviceAgent? _agent;
    private Timer? _watchdog;

    /// Raised when the renderer slot changes hands (or empties).
    public event Action? RendererChanged;
    /// Companion reported a volume capture (non-null) or a completed restore (null).
    public event Action<VolumeRecoveryRecord?>? VolumeOverrideReported;
    public Func<VolumeRecoveryRecord?>? PendingVolumeRecovery { get; set; }
    public Func<PipeServiceState>? ServiceState { get; set; }

    public PipeServerHost(ClientConfig cfg, ILogger log)
    {
        _cfg = cfg; _log = log;
    }

    public void Attach(DeviceAgent agent) => _agent = agent;

    public Session? Renderer { get { lock (_gate) return _renderer; } }

    public string CompanionState
    {
        get
        {
            lock (_gate)
            {
                if (_sessions.Count == 0) return "missing";
                return _sessions.Any(s => s.CompanionState == "running") ? "running" : "hidden";
            }
        }
    }

    // ---------- accept loop ----------

    public async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        _watchdog = new Timer(_ => Housekeeping(), null, 3_000, 3_000);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipeInstance();
                await pipe.WaitForConnectionAsync(ct);
                var (pid, winSession) = ClientIdentity(pipe);
                var session = new Session
                {
                    Pipe = pipe, WriteLock = new SemaphoreSlim(1, 1), LastSeenMs = NowMs(),
                    WindowsSession = winSession, ProcessId = pid,
                };
                lock (_gate) _sessions.Add(session);
                _log.LogInformation("companion connected (pid {Pid}, Windows session {Session})", pid, winSession);
                _ = HandleSessionAsync(session, ct);
                pipe = null;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _log.LogWarning("pipe instance failed: {Error} — retrying in 2 s", e.Message);
                pipe?.Dispose();
                try { await Task.Delay(2_000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        _watchdog?.Dispose();
    }

    private static NamedPipeServerStream CreatePipeInstance() =>
        NamedPipeServerStreamAcl.Create(
            PipeProtocol.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 8,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 16 * 1024, outBufferSize: 64 * 1024, BuildDacl());

    private static PipeSecurity BuildDacl()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return ps;
    }

    private static (int Pid, uint WinSession) ClientIdentity(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) return (0, uint.MaxValue);
        return ProcessIdToSessionId(pid, out var sid) ? ((int)pid, sid) : ((int)pid, uint.MaxValue);
    }

    public static uint ConsoleSession() => WTSGetActiveConsoleSessionId();

    // ---------- per-connection read loop ----------

    private async Task HandleSessionAsync(Session session, CancellationToken ct)
    {
        try
        {
            using (session.Pipe)
            using (var reader = new StreamReader(session.Pipe, Encoding.UTF8))
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    var msg = PipeProtocol.ParseLine(line);
                    if (msg is null)
                    {
                        _log.LogWarning("unparsable pipe line ignored (length {Len})", line.Length);
                        continue;
                    }
                    session.LastSeenMs = NowMs();
                    if (msg.Type == PipeProtocol.THello) HandleHello(session, msg);
                    else Route(session, msg);
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.LogInformation("companion pipe ended: {Error}", e.Message);
        }
        finally
        {
            bool wasRenderer;
            lock (_gate)
            {
                _sessions.Remove(session);
                wasRenderer = _renderer == session;
                if (wasRenderer) _renderer = null;
            }
            FailPendingBells();
            _log.LogInformation("companion disconnected (pid {Pid}, was renderer: {Renderer})", session.ProcessId, wasRenderer);
            if (wasRenderer) { Arbitrate(); RendererChanged?.Invoke(); }
            _agent?.NotifyStateChanged();
        }
    }

    private void HandleHello(Session session, PipeMessage msg)
    {
        var hello = msg.PayloadAs<PipeHello>() ?? new PipeHello("status_only", "");
        session.WantsRenderer = hello.Slot == "renderer";
        _log.LogInformation("companion hello (pid {Pid}, session {Session}, wants {Slot}, version {Version})",
            session.ProcessId, session.WindowsSession, hello.Slot, hello.ClientVersion);
        Arbitrate(); // may grant this session the renderer slot
        lock (_gate) if (!session.IsRenderer) SendHelloOk(session, "status_only");
        SendSettings(session);
        SendHistory(session);
        SendState(session);
        SendManifestState();
    }

    private void SendHelloOk(Session session, string slot) =>
        WriteLine(session, PipeProtocol.THelloOk, new PipeHelloOk(
            slot, _agent?.Cache.CurrentDir ?? "", _agent?.AnnouncementVolume ?? _cfg.AnnouncementVolume,
            _cfg.BellFadeMs, Environment.MachineName));

    /// Renderer slot goes to a companion in the active console session (the person at the
    /// classroom PC). A renderer that is no longer on the console (fast user switch) is demoted.
    private void Arbitrate()
    {
        Session? granted = null, demoted = null;
        var console = ConsoleSession();
        lock (_gate)
        {
            if (_renderer is not null && _renderer.WindowsSession != console)
            {
                demoted = _renderer;
                _renderer.IsRenderer = false;
                _renderer = null;
            }
            if (_renderer is null)
            {
                var candidate = _sessions.FirstOrDefault(s => s.WantsRenderer && s.WindowsSession == console);
                if (candidate is not null)
                {
                    candidate.IsRenderer = true;
                    _renderer = candidate;
                    granted = candidate;
                }
            }
        }
        if (demoted is not null)
        {
            _log.LogInformation("renderer slot taken from session {Session} (no longer the console session)", demoted.WindowsSession);
            SendHelloOk(demoted, "status_only");
        }
        if (granted is not null)
        {
            _log.LogInformation("renderer slot granted to companion in console session {Session}", granted.WindowsSession);
            SendHelloOk(granted, "renderer");
            if (PendingVolumeRecovery?.Invoke() is { } rec)
                WriteLine(granted, PipeTypes.VolumeRecover, new PipeVolumeRecover(rec));
        }
        if (granted is not null || demoted is not null) RendererChanged?.Invoke();
    }

    private void Route(Session session, PipeMessage msg)
    {
        switch (msg.Type)
        {
            case PipeProtocol.THeartbeat:
                break;

            case PipeProtocol.TStatusReport:
                var report = msg.PayloadAs<PipeStatusReport>();
                if (report is null) return;
                lock (_gate)
                {
                    session.CompanionState = report.CompanionState;
                    session.LastReport = report;
                }
                _agent?.NotifyStateChanged();
                break;

            case PipeProtocol.TBellResult:
                var result = msg.PayloadAs<PipeBellResult>();
                if (result is null || !session.IsRenderer) return;
                TaskCompletionSource<BellPlayOutcome>? tcs;
                lock (_pendingBells)
                {
                    _pendingBells.Remove(result.OccurrenceId, out tcs);
                }
                tcs?.TrySetResult(new BellPlayOutcome(
                    result.Result == "played", result.Result == "played" ? null : result.Reason ?? "renderer"));
                break;

            case PipeProtocol.TVolumeRestore:
                break; // informational (announcement-level restore); persistence uses volume.override

            case PipeTypes.VolumeOverride:
                if (!session.IsRenderer) return;
                VolumeOverrideReported?.Invoke(msg.PayloadAs<PipeVolumeOverride>()?.Record);
                break;

            case PipeTypes.HistoryGet:
                SendHistory(session);
                break;

            default:
                _log.LogWarning("ignoring unexpected pipe message '{Type}'", msg.Type);
                break;
        }
    }

    private void Housekeeping()
    {
        Session? dead = null;
        lock (_gate)
        {
            if (_renderer is not null && NowMs() - _renderer.LastSeenMs > PipeProtocol.RendererSlotReclaimMs)
            {
                dead = _renderer;
                _renderer = null;
            }
        }
        if (dead is not null)
        {
            _log.LogWarning("renderer heartbeat silent >{Ms} ms — slot reclaimed", PipeProtocol.RendererSlotReclaimMs);
            try { dead.Pipe.Close(); } catch { }
            FailPendingBells();
            RendererChanged?.Invoke();
        }
        Arbitrate(); // picks up console-session changes (sign-in, fast user switching)
    }

    private void FailPendingBells()
    {
        List<TaskCompletionSource<BellPlayOutcome>> pending;
        lock (_pendingBells)
        {
            pending = _pendingBells.Values.ToList();
            _pendingBells.Clear();
        }
        foreach (var p in pending) p.TrySetResult(new(false, "renderer_lost"));
    }

    // ---------- companion renderer transport (used by RendererRouter) ----------

    public async Task<BellPlayOutcome> PlayOnCompanionAsync(Session session, PipeBellPlay play, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BellPlayOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingBells) _pendingBells[play.OccurrenceId] = tcs;
        try
        {
            if (!WriteLine(session, PipeProtocol.TBellPlay, play)) return new(false, "renderer_lost");
            var budget = play.DurationS * 1000 + 15_000;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(budget, ct));
            return done == tcs.Task ? await tcs.Task : new(false, "renderer_timeout");
        }
        finally
        {
            lock (_pendingBells) _pendingBells.Remove(play.OccurrenceId);
        }
    }

    public bool SendAnnStart(Session s, PipeAnnStart start) => WriteLine(s, PipeProtocol.TAnnStart, start);

    public void SendAnnFrames(Session s, string announcementId, IReadOnlyList<MediaFrame> frames)
    {
        for (int i = 0; i < frames.Count; i += PipeProtocol.MaxFramesPerBatch)
        {
            var batch = frames.Skip(i).Take(PipeProtocol.MaxFramesPerBatch)
                .Select(f => new PipeFrameLine(f.Seq, f.CaptureTsMs, f.Flags, Convert.ToBase64String(f.Pcm.Span)))
                .ToList();
            WriteLine(s, PipeProtocol.TAnnFrames, new PipeAnnFrames(announcementId, batch));
        }
    }

    public void SendAnnEnd(Session s, string announcementId, string endedBy) =>
        WriteLine(s, PipeProtocol.TAnnEnd, new PipeAnnEnd(announcementId, endedBy));

    // ---------- service-originated pushes ----------

    public void SendManifestState()
    {
        var agent = _agent;
        if (agent is null) return;
        var manifest = agent.Manifests.Current;
        var next = agent.PeekNextOccurrence();
        var state = new PipeManifestState(manifest?.Revision ?? 0, manifest?.ExpiresAt ?? 0, agent.StaleCache,
            next is null ? null : new PipeNextOccurrence(next.OccurrenceId, next.BellName, next.StartsAt, next.LocalHm));
        foreach (var s in Snapshot()) WriteLine(s, PipeProtocol.TManifestState, state);
    }

    public void SendKeepAwakeState()
    {
        var agent = _agent;
        if (agent is null) return;
        var state = agent.KeepAwakePolicies.Evaluate(agent.Clock.NowMs());
        foreach (var s in Snapshot())
            WriteLine(s, PipeProtocol.TKeepAwakeState, new PipeKeepAwakeState(state.Desired, state.DisplayOff, state.MaxExpiresAt));
    }

    public void BroadcastSettings() { foreach (var s in Snapshot()) SendSettings(s); }
    public void BroadcastHistory() { foreach (var s in Snapshot()) SendHistory(s); }
    public void BroadcastState() { foreach (var s in Snapshot()) SendState(s); }

    private void SendSettings(Session s)
    {
        var st = _agent?.Settings.Current;
        WriteLine(s, PipeTypes.Settings, new PipeSettings(st?.OutputDevice, _agent?.AnnouncementVolume ?? _cfg.AnnouncementVolume,
            st?.VolumeCap ?? 100));
    }

    private void SendHistory(Session s)
    {
        if (_agent is null) return;
        var items = _agent.History.Snapshot();
        // keep each line under the pipe limit: newest first, trimmed if very long
        while (items.Count > 0)
        {
            try { WriteLine(s, PipeTypes.HistoryList, new PipeHistoryList(items), rethrowSize: true); return; }
            catch (InvalidOperationException) { items = items.Take(items.Count / 2).ToList(); }
        }
        WriteLine(s, PipeTypes.HistoryList, new PipeHistoryList([]));
    }

    private void SendState(Session s)
    {
        if (ServiceState?.Invoke() is { } st) WriteLine(s, PipeTypes.ServiceState, st);
    }

    private List<Session> Snapshot() { lock (_gate) return _sessions.ToList(); }

    private bool WriteLine(Session session, string type, object payload, bool rethrowSize = false)
    {
        string line;
        try { line = PipeProtocol.SerializeLine(type, payload); }
        catch (InvalidOperationException) when (rethrowSize) { throw; }
        catch (InvalidOperationException e) { _log.LogWarning("pipe line too large ({Type}): {Error}", type, e.Message); return false; }
        try
        {
            if (!session.Pipe.IsConnected) return false;
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            session.WriteLock.Wait();
            try
            {
                session.Pipe.Write(bytes);
                session.Pipe.Flush();
            }
            finally { session.WriteLock.Release(); }
            return true;
        }
        catch (Exception e)
        {
            _log.LogInformation("pipe write failed ({Type}): {Error}", type, e.Message);
            return false;
        }
    }

    private static long NowMs() => Environment.TickCount64;

    public void Dispose()
    {
        _watchdog?.Dispose();
        foreach (var s in Snapshot()) try { s.Pipe.Close(); } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}

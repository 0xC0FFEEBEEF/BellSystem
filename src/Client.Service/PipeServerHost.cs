using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Service;

/// Named pipe server \\.\pipe\schoolbell-v1 (PIPE.md). Local-only by pipe nature;
/// DACL = SYSTEM + Administrators + the active console session user (fallback
/// INTERACTIVE when signed out); anonymous logons are not granted and are rejected.
/// One renderer slot: the first companion claiming `renderer` holds it; others are
/// status_only. The slot is reclaimed if the renderer heartbeat (3 s) goes silent >12 s.
/// Windows-only glue — compile-verified on Linux, behavior-verified on Windows.
public sealed class PipeServerHost : IBellPlayer, IAnnouncementPipeline, IRendererProvider, IDisposable
{
    private sealed class Session
    {
        public required NamedPipeServerStream Pipe { get; init; }
        public required SemaphoreSlim WriteLock { get; init; }
        public bool IsRenderer { get; set; }
        public long LastSeenMs { get; set; }
        public string CompanionState { get; set; } = "running"; // running | hidden
    }

    private readonly ClientConfig _cfg;
    private readonly ActiveUserProvider _users;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<BellPlayOutcome>> _pendingBells = [];
    private readonly List<Session> _sessions = [];
    private Session? _renderer;
    private DeviceAgent? _agent;
    private RendererInfo _rendererFacts = new("no_renderer", "renderer_slot_open", null, false, false);
    private VolumeSnapshot _volumes = new(false, 0, false, 0);
    private Timer? _watchdog;

    public PipeServerHost(ClientConfig cfg, ActiveUserProvider users, ILogger log)
    {
        _cfg = cfg; _users = users; _log = log;
    }

    public void Attach(DeviceAgent agent) => _agent = agent;

    // ---------- IRendererProvider (device.status facts) ----------

    public RendererInfo GetRenderer()
    {
        lock (_gate)
        {
            if (_renderer is null)
                return new("no_renderer", "renderer_slot_open", null, false, false);
            return _rendererFacts;
        }
    }

    public VolumeSnapshot GetVolumes() { lock (_gate) return _volumes; }

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
        _watchdog = new Timer(CheckRendererHeartbeat, null, 3_000, 3_000);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipeInstance();
                await pipe.WaitForConnectionAsync(ct);
                var session = new Session { Pipe = pipe, WriteLock = new SemaphoreSlim(1, 1), LastSeenMs = NowMs() };
                lock (_gate) _sessions.Add(session);
                _log.LogInformation("companion connected on {Pipe}", PipeProtocol.PipeName);
                _ = HandleSessionAsync(session, ct); // one task per connection
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

    private NamedPipeServerStream CreatePipeInstance()
    {
        return NamedPipeServerStreamAcl.Create(
            PipeProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 16 * 1024,
            outBufferSize: 64 * 1024,
            BuildDacl());
    }

    private PipeSecurity BuildDacl()
    {
        var ps = new PipeSecurity();
        AddAllow(ps, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddAllow(ps, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        // The interactive session user gets access through their own SID, so only that
        // user's companion processes connect; anonymous logons are not granted.
        var activeSid = ActiveUserSid();
        AddAllow(ps, activeSid ?? new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        return ps;
    }

    private static void AddAllow(PipeSecurity ps, SecurityIdentifier sid) =>
        ps.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

    private SecurityIdentifier? ActiveUserSid()
    {
        try
        {
            var sidString = _users.GetActiveUserSidString();
            return sidString is null ? null : new SecurityIdentifier(sidString);
        }
        catch (Exception e)
        {
            _log.LogWarning("active-user SID lookup for pipe DACL failed: {Error}", e.Message);
            return null;
        }
    }

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
                    if (line is null) break; // companion closed
                    var msg = PipeProtocol.ParseLine(line);
                    if (msg is null)
                    {
                        _log.LogWarning("unparsable pipe line ignored (length {Len})", line.Length);
                        continue;
                    }
                    if (msg.Type == PipeProtocol.THello) HandleHello(session, msg);
                    else RouteToHandler(session, msg);
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.LogInformation("companion pipe ended: {Error}", e.Message);
        }
        finally
        {
            lock (_gate)
            {
                _sessions.Remove(session);
                if (_renderer == session)
                {
                    _renderer = null;
                    _rendererFacts = new("no_renderer", "renderer_slot_open", null, false, false);
                    _agent?.NotifyStateChanged();
                }
            }
            _log.LogInformation("companion disconnected (was renderer: {Renderer})", session.IsRenderer);
        }
    }

    private void HandleHello(Session session, PipeMessage msg)
    {
        var hello = msg.PayloadAs<PipeHello>() ?? new PipeHello("status_only", "");
        var wantsRenderer = hello.Slot == "renderer";
        string granted;
        lock (_gate)
        {
            if (wantsRenderer && _renderer is null)
            {
                _renderer = session;
                session.IsRenderer = true;
                granted = "renderer";
            }
            else
            {
                granted = "status_only";
            }
        }
        WriteLine(session, PipeProtocol.THelloOk, new PipeHelloOk(
            granted, _agent?.Cache.CurrentDir ?? "", _cfg.AnnouncementVolume, _cfg.BellFadeMs,
            Environment.MachineName));
        _log.LogInformation("companion hello: claimed {Claimed}, granted {Granted} (client {Version})",
            hello.Slot, granted, hello.ClientVersion);
        if (session.IsRenderer) SendManifestState(); // give the new renderer current facts
    }

    private void RouteToHandler(Session session, PipeMessage msg)
    {
        switch (msg.Type)
        {
            case PipeProtocol.THeartbeat:
                session.LastSeenMs = NowMs();
                break;

            case PipeProtocol.TStatusReport:
                var report = msg.PayloadAs<PipeStatusReport>();
                if (report is null) return;
                lock (_gate)
                {
                    session.CompanionState = report.CompanionState;
                    if (session.IsRenderer)
                    {
                        _rendererFacts = new RendererInfo(report.Renderer, report.RendererReason,
                            report.OutputDevice, report.OutputPresent, VolumeLocked: false);
                        _volumes = new VolumeSnapshot(report.MasterMuted, report.MasterVolume,
                            report.SessionMuted, report.SessionVolume);
                    }
                }
                _agent?.NotifyStateChanged();
                break;

            case PipeProtocol.TBellResult:
                var result = msg.PayloadAs<PipeBellResult>();
                if (result is null) return;
                TaskCompletionSource<BellPlayOutcome>? tcs;
                lock (_pendingBells)
                {
                    _pendingBells.TryGetValue(result.OccurrenceId, out tcs);
                    _pendingBells.Remove(result.OccurrenceId);
                }
                tcs?.TrySetResult(new BellPlayOutcome(
                    result.Result == "played", result.Result == "played" ? null : result.Reason ?? "renderer"));
                break;

            case PipeProtocol.TVolumeRestore:
                var restore = msg.PayloadAs<PipeVolumeRestore>();
                if (restore is not null)
                    _log.LogInformation("volume override restored for announcement {AnnouncementId}",
                        restore.AnnouncementId);
                break;

            default:
                _log.LogWarning("ignoring unexpected pipe message '{Type}' (companion may only send PIPE.md messages)", msg.Type);
                break;
        }
    }

    // ---------- watchdog: reclaim the renderer slot after 12 s of silence ----------

    private void CheckRendererHeartbeat(object? state)
    {
        Session? dead = null;
        lock (_gate)
        {
            if (_renderer is not null && NowMs() - _renderer.LastSeenMs > PipeProtocol.RendererSlotReclaimMs)
            {
                dead = _renderer;
                _renderer = null;
                _rendererFacts = new("no_renderer", "renderer_slot_open", null, false, false);
            }
        }
        if (dead is null) return;
        _log.LogWarning("renderer heartbeat silent >{Ms} ms — slot reclaimed", PipeProtocol.RendererSlotReclaimMs);
        try { dead.Pipe.Close(); } catch { }
        _agent?.NotifyStateChanged();
    }

    // ---------- IBellPlayer ----------

    public async Task<BellPlayOutcome> PlayAsync(PipeBellPlay play, CancellationToken ct)
    {
        Session? session;
        lock (_gate) session = _renderer;
        if (session is null) return new(false, "no_renderer");
        var tcs = new TaskCompletionSource<BellPlayOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingBells) _pendingBells[play.OccurrenceId] = tcs;
        try
        {
            WriteLine(session, PipeProtocol.TBellPlay, play);
            var budget = play.DurationS * 1000 + 15_000; // render time + generous slack
            var done = await Task.WhenAny(tcs.Task, Task.Delay(budget, CancellationToken.None));
            if (done != tcs.Task)
                return new(false, "renderer_timeout");
            return await tcs.Task;
        }
        finally
        {
            lock (_pendingBells) _pendingBells.Remove(play.OccurrenceId);
        }
    }

    // ---------- IAnnouncementPipeline ----------

    public Task StartAsync(PipeAnnStart start, CancellationToken ct)
    {
        var session = RendererOrNull() ?? throw new InvalidOperationException("no renderer companion");
        WriteLine(session, PipeProtocol.TAnnStart, start);
        return Task.CompletedTask;
    }

    public async Task FramesAsync(string announcementId, IReadOnlyList<MediaFrame> frames, CancellationToken ct)
    {
        var session = RendererOrNull();
        if (session is null) return; // live announcement without renderer was already rejected at offer time
        for (int i = 0; i < frames.Count; i += PipeProtocol.MaxFramesPerBatch)
        {
            var batch = frames.Skip(i).Take(PipeProtocol.MaxFramesPerBatch)
                .Select(f => new PipeFrameLine(f.Seq, f.CaptureTsMs, f.Flags, Convert.ToBase64String(f.Pcm.ToArray())))
                .ToList();
            WriteLine(session, PipeProtocol.TAnnFrames, new PipeAnnFrames(announcementId, batch));
            if (batch.Count > 0 && i + PipeProtocol.MaxFramesPerBatch < frames.Count)
                await Task.Yield();
        }
    }

    public Task EndAsync(string announcementId, string endedBy, CancellationToken ct)
    {
        var session = RendererOrNull();
        if (session is null) return Task.CompletedTask;
        WriteLine(session, PipeProtocol.TAnnEnd, new PipeAnnEnd(announcementId, endedBy));
        return Task.CompletedTask;
    }

    // ---------- service-originated state pushes ----------

    public void SendManifestState()
    {
        var agent = _agent;
        if (agent is null) return;
        var manifest = agent.Manifests.Current;
        var next = agent.PeekNextOccurrence();
        var state = new PipeManifestState(
            manifest?.Revision ?? 0,
            manifest?.ExpiresAt ?? 0,
            agent.StaleCache,
            next is null ? null : new PipeNextOccurrence(next.OccurrenceId, next.BellName, next.StartsAt, next.LocalHm));
        foreach (var session in SnapshotSessions())
            WriteLine(session, PipeProtocol.TManifestState, state);
    }

    public void SendKeepAwakeState()
    {
        var agent = _agent;
        if (agent is null) return;
        var state = agent.KeepAwakePolicies.Evaluate(agent.Clock.NowMs());
        foreach (var session in SnapshotSessions())
            WriteLine(session, PipeProtocol.TKeepAwakeState,
                new PipeKeepAwakeState(state.Desired, state.DisplayOff, state.MaxExpiresAt));
    }

    private List<Session> SnapshotSessions() { lock (_gate) return _sessions.ToList(); }

    private Session? RendererOrNull() { lock (_gate) return _renderer; }

    private void WriteLine(Session session, string type, object payload)
    {
        try
        {
            if (!session.Pipe.CanWrite) return;
            var line = PipeProtocol.SerializeLine(type, payload);
            session.WriteLock.Wait();
            try
            {
                using var writer = new StreamWriter(session.Pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(line);
            }
            finally { session.WriteLock.Release(); }
        }
        catch (Exception e)
        {
            _log.LogInformation("pipe write failed ({Type}): {Error}", type, e.Message);
        }
    }

    private static long NowMs() => Environment.TickCount64;

    public void Dispose()
    {
        _watchdog?.Dispose();
        lock (_gate)
            foreach (var s in _sessions.ToList())
                try { s.Pipe.Close(); } catch { }
    }
}

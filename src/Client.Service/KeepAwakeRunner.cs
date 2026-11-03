using System.Threading.Channels;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;

namespace SchoolBell.Client.Service;

/// Keep-awake per PROTOCOL §9: SetThreadExecutionState is applied AND cleared on ONE
/// dedicated thread; policies are re-evaluated on request and every 30 s so an expired
/// policy is released even while offline. Never touches the machine's power plan.
/// Documented limits (Microsoft): does not defeat manual sleep, lid close, shutdown or
/// group policy. Windows-only glue — compile-verified on Linux.
public sealed class KeepAwakeRunner : IKeepAwakeApplier, IDisposable
{
    private const uint EsContinuous = 0x8000_0000;
    private const uint EsSystemRequired = 0x0000_0001;
    private const uint EsDisplayRequired = 0x0000_0002;

    private sealed record Request(bool Clear, bool DisplayOff, TaskCompletionSource<bool> Reply);

    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    private KeepAwakePolicyStore? _store;
    private BellClock? _clock;
    private ILogger? _log;

    private volatile bool _applied;
    private bool _displayOff;

    public bool Applied => _applied;

    public void Configure(KeepAwakePolicyStore store, BellClock clock, ILogger log)
    {
        _store = store; _clock = clock; _log = log;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "SchoolBell-KeepAwake" };
        _thread.Start();
    }

    public async Task<bool> RequestApplyAsync(bool displayOff, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _requests.Writer.WriteAsync(new Request(Clear: false, displayOff, tcs), ct);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct));
        if (done != tcs.Task) throw new OperationCanceledException(ct);
        return tcs.Task.Result;
    }

    public async Task RequestClearAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _requests.Writer.WriteAsync(new Request(Clear: true, DisplayOff: false, tcs), ct);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct));
        if (done != tcs.Task) throw new OperationCanceledException(ct);
    }

    // Everything below runs on the single dedicated thread (apply and clear same-thread).
    private void RunLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // 1) drain explicit requests (set/clear from control messages)
                while (_requests.Reader.TryRead(out var req))
                {
                    if (req.Clear) ClearState();
                    else ApplyState(req.DisplayOff);
                    req.Reply.TrySetResult(_applied);
                }

                // 2) expiry check even offline: release at expires_at without any server contact
                if (_store is not null && _clock is not null)
                {
                    var state = _store.Evaluate(_clock.NowMs());
                    if (!state.Desired && _applied)
                    {
                        ClearState();
                        _log?.LogInformation("keep-awake policy expired — released (offline-safe local evaluation)");
                    }
                    else if (state.Desired && (!_applied || state.DisplayOff != _displayOff))
                        ApplyState(state.DisplayOff);
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning("keep-awake loop iteration failed: {Error}", e.Message);
            }

            // 3) wait up to 30 s for the next request
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _requests.Reader.WaitToReadAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { /* 30 s tick */ }
            catch (Exception) { }
        }

        ClearState(); // service stopping: release on the same thread we applied on
    }

    private void ApplyState(bool displayOff)
    {
        uint flags = EsContinuous | EsSystemRequired | (displayOff ? EsDisplayRequired : 0);
        var result = SetThreadExecutionState(flags);
        _applied = result != 0;
        _displayOff = displayOff && _applied;
        if (!_applied)
            _log?.LogWarning("SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED{Display}) failed — keep-awake not active",
                displayOff ? "|ES_DISPLAY_REQUIRED" : "");
    }

    private void ClearState()
    {
        if (!_applied) return;
        SetThreadExecutionState(EsContinuous); // clears the previous request on this same thread
        _applied = false;
        _displayOff = false;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _requests.Writer.TryComplete();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}

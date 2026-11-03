namespace SchoolBell.Client.Core;

/// net8 TimeProvider exposes GetTimestamp/GetElapsedTime/CreateTimer but no Delay —
/// this helper builds a cancellable one-shot delay on top of CreateTimer so all
/// scheduling in Client.Core stays testable with a fake TimeProvider.
public static class TimeProviderUtil
{
    public static Task DelayAsync(this TimeProvider time, TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctReg = ct.Register(() => tcs.TrySetCanceled(ct));
        var timer = time.CreateTimer(_ => tcs.TrySetResult(), state: null, delay, Timeout.InfiniteTimeSpan);
        return tcs.Task.ContinueWith(t => { timer.Dispose(); return t; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Current)
            .Unwrap();
    }
}

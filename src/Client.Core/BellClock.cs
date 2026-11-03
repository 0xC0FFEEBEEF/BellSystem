namespace SchoolBell.Client.Core;

/// Server-corrected MONOTONIC clock (PROTOCOL §6): the offset from auth.ok
/// `server_time` is captured once per connection; afterwards the corrected time is
/// derived only from monotonic elapsed time, never re-resolved from the wall clock
/// mid-wait. Monotonic elapsed survives sleep/reboot (the counter keeps counting
/// wall-clock time while suspended), so a long sleep shows up as a large jump —
/// which the scheduler turns into an expired batch, never a late replay.
public sealed class BellClock
{
    private readonly TimeProvider _time;
    private long _refServerMs;
    private long _refStamp;
    private bool _synced;
    private readonly object _gate = new();

    public BellClock(TimeProvider time)
    {
        _time = time;
        // Before the first auth.ok the local wall clock is used with offset 0;
        // it is re-based on every handshake, never mid-wait.
        _refServerMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        _refStamp = time.GetTimestamp();
    }

    /// Called on each auth.ok only.
    public void Sync(long serverTimeMs)
    {
        lock (_gate)
        {
            _refServerMs = serverTimeMs;
            _refStamp = _time.GetTimestamp();
            _synced = true;
        }
    }

    public bool Synced { get { lock (_gate) return _synced; } }

    /// Corrected UTC Unix ms — monotonic past the sync point.
    public long NowMs()
    {
        lock (_gate)
            return _refServerMs + ElapsedMsSince(_refStamp);
    }

    /// clock_offset_ms for device.status: corrected "now" minus local wall clock.
    public long OffsetMs()
    {
        lock (_gate)
        {
            var wall = _time.GetUtcNow().ToUnixTimeMilliseconds();
            return _refServerMs + ElapsedMsSince(_refStamp) - wall;
        }
    }

    private long ElapsedMsSince(long fromStamp)
    {
        var now = _time.GetTimestamp();
        var freq = _time.TimestampFrequency;
        // Multiply first for precision, matching TimeProvider.GetElapsedTime behavior.
        return (now - fromStamp) * 1000 / freq;
    }
}

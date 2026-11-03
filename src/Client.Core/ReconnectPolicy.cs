namespace SchoolBell.Client.Core;

/// Exponential backoff with FULL jitter (PROTOCOL §3): delay for attempt n (1-based)
/// is uniform on [0, min(cap, base * 2^(n-1))]. Cap default 30 s. The attempt counter
/// resets after a connection that stayed stable for ≥60 s. Single caller loop enforces
/// "no parallel reconnect loops"; this class is stateless about connections.
public sealed class ReconnectPolicy
{
    private readonly TimeSpan _cap;
    private readonly TimeSpan _base;
    private readonly Random _rng;
    private int _attempt;

    public ReconnectPolicy(TimeSpan? cap = null, TimeSpan? @base = null, Random? rng = null)
    {
        _cap = cap ?? TimeSpan.FromSeconds(Contracts.Protocol.ReconnectCapSeconds);
        _base = @base ?? TimeSpan.FromSeconds(1);
        _rng = rng ?? Random.Shared;
    }

    public int Attempt { get { lock (_rng) return _attempt; } }

    /// Call once per failed connection attempt (the first connection attempt itself
    /// is immediate — call this only AFTER a failure).
    public TimeSpan NextDelay()
    {
        lock (_rng)
        {
            _attempt++;
            double expo = _base.TotalMilliseconds * Math.Pow(2, _attempt - 1);
            double ceiling = Math.Min(_cap.TotalMilliseconds, expo);
            return TimeSpan.FromMilliseconds(_rng.NextDouble() * ceiling);
        }
    }

    /// Call when a connection was dropped after being stable for ≥ this many ms (default 60 s).
    public void ReportStableConnection(long stableMs, long stableResetMs = 60_000)
    {
        if (stableMs < stableResetMs) return;
        lock (_rng) _attempt = 0;
    }

    /// Test/observability helper: the jitter ceiling for attempt n (1-based).
    public double CeilingMsForAttempt(int attempt)
    {
        double expo = _base.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return Math.Min(_cap.TotalMilliseconds, expo);
    }
}

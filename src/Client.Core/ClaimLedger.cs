namespace SchoolBell.Client.Core;

public sealed record ClaimEntry(string OccurrenceId, long ClaimedAt, string? TerminalState);

/// Durable occurrence claim ledger (PROTOCOL §6 "claim before playback").
/// A claim is written BEFORE any playback instruction; duplicate triggers find the
/// claim and do nothing. Claims without a terminal state after a crash surface as
/// `unknown` receipts — the system never claims audible exactly-once.
public sealed class ClaimLedger
{
    private sealed class LedgerData { public List<ClaimEntry> Claims { get; set; } = []; }

    private readonly JsonStore<LedgerData> _store;
    private readonly object _gate = new();
    private LedgerData _data;

    public ClaimLedger(string dataDir)
    {
        _store = new JsonStore<LedgerData>(System.IO.Path.Combine(dataDir, "ledger.json"));
        _data = _store.Load() ?? new LedgerData();
    }

    public int Count { get { lock (_gate) return _data.Claims.Count; } }

    /// True when THIS call created the claim; false when one already existed (dedupe).
    public bool TryClaim(string occurrenceId, long nowMs)
    {
        lock (_gate)
        {
            if (_data.Claims.Any(c => c.OccurrenceId == occurrenceId)) return false;
            _data.Claims.Add(new ClaimEntry(occurrenceId, nowMs, null));
            _store.Save(_data);
            return true;
        }
    }

    public bool Has(string occurrenceId)
    {
        lock (_gate) return _data.Claims.Any(c => c.OccurrenceId == occurrenceId);
    }

    public void MarkTerminal(string occurrenceId, string terminalState)
    {
        lock (_gate)
        {
            var i = _data.Claims.FindIndex(c => c.OccurrenceId == occurrenceId);
            if (i < 0) return;
            _data.Claims[i] = _data.Claims[i] with { TerminalState = terminalState };
            _store.Save(_data);
        }
    }

    /// Claims left non-terminal (crash between claim and playback outcome).
    public List<ClaimEntry> UnterminalClaims()
    {
        lock (_gate) return _data.Claims.Where(c => c.TerminalState is null).ToList();
    }

    /// Keeps claims that can still matter: all non-terminal ones, plus terminal claims
    /// claimed at or after cutoff. Callers pass the current manifest's horizon_start, so
    /// terminal claims from already-superseded manifests are dropped.
    public void PruneTerminalBefore(long cutoffMs)
    {
        lock (_gate)
        {
            var before = _data.Claims.Count;
            _data.Claims.RemoveAll(c => c.TerminalState is not null && c.ClaimedAt < cutoffMs);
            if (_data.Claims.Count != before) _store.Save(_data);
        }
    }
}

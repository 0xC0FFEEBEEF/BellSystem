namespace SchoolBell.Client.Core;

public sealed record KeepAwakePolicy(string PolicyId, long ExpiresAt, bool DisplayOff);
public sealed record KeepAwakeState(bool Desired, bool DisplayOff, long MaxExpiresAt);

/// Durable keep-awake policies (PROTOCOL §9): {policy_id, absolute expires_at,
/// display_off}. Active = now < expires_at; re-evaluated on load, set and clear.
/// Because expiry is a pure local comparison against the monotonic clock, a policy is
/// released at expiry EVEN OFFLINE. Display-off stays a separate option (default off).
public sealed class KeepAwakePolicyStore
{
    private sealed class PoliciesData { public List<KeepAwakePolicy> Policies { get; set; } = []; }

    private readonly JsonStore<PoliciesData> _store;
    private readonly object _gate = new();
    private PoliciesData _data;

    public KeepAwakePolicyStore(string dataDir)
    {
        _store = new JsonStore<PoliciesData>(System.IO.Path.Combine(dataDir, "keepawake.json"));
        _data = _store.Load() ?? new PoliciesData();
    }

    public void Set(string policyId, long expiresAt, bool displayOff)
    {
        lock (_gate)
        {
            _data.Policies.RemoveAll(p => p.PolicyId == policyId);
            _data.Policies.Add(new KeepAwakePolicy(policyId, expiresAt, displayOff));
            _store.Save(_data);
        }
    }

    public bool Clear(string policyId)
    {
        lock (_gate)
        {
            var removed = _data.Policies.RemoveAll(p => p.PolicyId == policyId);
            if (removed > 0) _store.Save(_data);
            return removed > 0;
        }
    }

    public IReadOnlyList<KeepAwakePolicy> All()
    {
        lock (_gate) return _data.Policies.ToList();
    }

    /// Active policies at nowMs; expired ones are durably dropped here (the offline release path).
    public KeepAwakeState Evaluate(long nowMs)
    {
        lock (_gate)
        {
            var dropped = _data.Policies.RemoveAll(p => p.ExpiresAt <= nowMs);
            if (dropped > 0) _store.Save(_data);
            var active = _data.Policies;
            return new(
                Desired: active.Count > 0,
                DisplayOff: active.Any(p => p.DisplayOff),
                MaxExpiresAt: active.Count == 0 ? 0 : active.Max(p => p.ExpiresAt));
        }
    }
}

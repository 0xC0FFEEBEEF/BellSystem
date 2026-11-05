using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public enum BellAction { Play, Expired, Suppressed }

public sealed record BellDecision(string OccurrenceId, BellAction Action, Occurrence Occurrence);

public sealed record BellEvaluation(
    List<BellDecision> Decisions,
    long? NextDueMs,          // earliest future starts_at, null if none in horizon
    bool StaleCache);         // manifest expired: stop scheduling, never guess the calendar

/// Pure decision core for cached bell execution (PROTOCOL §6). Given the accepted
/// manifest, the corrected monotonic "now" and whether a live announcement is active:
/// - due (starts_at ≤ now ≤ starts_at+grace) → Play (claims first) — or Suppressed
///   when live speech is active (never rings later);
/// - now > starts_at+grace → Expired (claim + expired receipt, never replayed late);
///   a long sleep/reboot surfaces the whole past horizon as one expired batch;
/// - duplicate triggers hit the claim ledger and produce nothing;
/// - manifest past expires_at → StaleCache.
public sealed class BellScheduler
{
    private readonly ClaimLedger _ledger;
    private readonly int _graceMs;

    public BellScheduler(ClaimLedger ledger, int graceSeconds)
    {
        _ledger = ledger;
        _graceMs = Math.Clamp(graceSeconds, 0, 60) * 1000;
    }

    public BellEvaluation Evaluate(ScheduleManifestPayload? manifest, long nowMs, bool liveActive,
        string deviceId, IEnumerable<long> deviceGroups)
    {
        var decisions = new List<BellDecision>();
        if (manifest is null) return new(decisions, null, StaleCache: false);

        if (nowMs > manifest.ExpiresAt)
            return new(decisions, null, StaleCache: true);

        var groups = deviceGroups as IReadOnlyCollection<long> ?? deviceGroups.ToList();
        long? nextDue = null;

        // Ascending by time so the past-horizon batch is deterministic and ordered.
        foreach (var occ in manifest.Occurrences.OrderBy(o => o.StartsAt))
        {
            if (!occ.Target.Matches(deviceId, groups)) continue;
            if (_ledger.Has(occ.OccurrenceId)) continue; // duplicate trigger / already handled
            if (occ.ExpiresAt <= nowMs) continue;        // past its own expiry: drop silently
            // already past when the schedule was published (the horizon starts at midnight): nobody
            // could ring it under this manifest, so it is not reported as missed
            if (occ.StartsAt < manifest.GeneratedAt) continue;

            if (occ.StartsAt > nowMs)
            {
                nextDue ??= occ.StartsAt; // first future occurrence in the horizon
                continue;
            }
            if (nowMs - occ.StartsAt > _graceMs)
            {
                if (_ledger.TryClaim(occ.OccurrenceId, nowMs))
                    decisions.Add(new(occ.OccurrenceId, BellAction.Expired, occ));
                continue;
            }
            // Within grace window: due.
            if (!_ledger.TryClaim(occ.OccurrenceId, nowMs)) continue;
            decisions.Add(new(occ.OccurrenceId, liveActive ? BellAction.Suppressed : BellAction.Play, occ));
        }

        return new(decisions, nextDue, StaleCache: false);
    }
}

using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public sealed record ManifestAcceptResult(bool Accepted, long Revision, string? Reason);

/// Durable manifest store: keeps ONLY the newest accepted revision. A candidate is
/// first verified (signature against the device verify key derived from the trust pin,
/// and revision > local) BEFORE it is used anywhere; it is committed only after the
/// caller has finished the atomic sound-cache swap (PROTOCOL §5 acceptance is atomic).
/// Rejections (signature/revision) are reported by the caller as manifest.rejected —
/// a rejected manifest never replaces the accepted one.
public sealed class ManifestStore
{
    private sealed class StoredManifest
    {
        public ScheduleManifestPayload Manifest { get; set; } = new();
        public long AcceptedAt { get; set; }
    }

    private readonly JsonStore<StoredManifest> _store;
    private readonly object _gate = new();
    private StoredManifest? _stored;

    public ManifestStore(string dataDir)
    {
        _store = new JsonStore<StoredManifest>(System.IO.Path.Combine(dataDir, "manifest.json"));
        _stored = _store.Load();
        if (_stored?.Manifest is null || _stored.Manifest.Revision <= 0) _stored = null;
    }

    public ScheduleManifestPayload? Current { get { lock (_gate) return _stored?.Manifest; } }
    public long LocalRevision { get { lock (_gate) return _stored?.Manifest.Revision ?? 0; } }

    /// Check-only (no state change): ok before use, with a reason for manifest.rejected.
    public ManifestAcceptResult VerifyCandidate(ScheduleManifestPayload manifest, byte[] deviceVerifyKey)
    {
        lock (_gate)
        {
            if (manifest.Revision <= LocalRevision)
                return new(false, manifest.Revision, "revision_not_newer");
            if (!ManifestSigner.Verify(manifest, deviceVerifyKey))
                return new(false, manifest.Revision, "signature_invalid");
            return new(true, manifest.Revision, null);
        }
    }

    /// Commit AFTER the sound cache swap succeeded; keeps only the newest accepted revision.
    public void Commit(ScheduleManifestPayload manifest, long nowMs)
    {
        lock (_gate)
        {
            if (manifest.Revision <= LocalRevision) return;
            _stored = new StoredManifest { Manifest = manifest, AcceptedAt = nowMs };
            _store.Save(_stored);
        }
    }
}

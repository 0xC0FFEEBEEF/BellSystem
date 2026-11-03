using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public sealed record ManifestAcceptResult(bool Accepted, long Revision, string? Reason);

/// Durable manifest store: keeps ONLY the newest accepted revision and accepts a newer
/// manifest only when revision > local AND the signature verifies against the device
/// verify key derived from the trust pin (PROTOCOL §5). Rejections are reported by the
/// caller as manifest.rejected — a rejected manifest never replaces the accepted one.
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

    /// Verify-and-accept (signature checked BEFORE the manifest is used anywhere).
    public ManifestAcceptResult TryAccept(ScheduleManifestPayload manifest, byte[] deviceVerifyKey, long nowMs)
    {
        lock (_gate)
        {
            if (manifest.Revision <= LocalRevision)
                return new(false, manifest.Revision, "revision_not_newer");
            if (!ManifestSigner.Verify(manifest, deviceVerifyKey))
                return new(false, manifest.Revision, "signature_invalid");
            _stored = new StoredManifest { Manifest = manifest, AcceptedAt = nowMs };
            _store.Save(_stored);
            return new(true, manifest.Revision, null);
        }
    }
}

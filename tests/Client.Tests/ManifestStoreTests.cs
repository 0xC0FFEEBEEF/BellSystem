using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class ManifestStoreTests
{
    [Fact]
    public void Rejects_revision_not_newer()
    {
        var dir = TestDirs.Create("manifest-rev");
        try
        {
            var key = ManifestTestKit.DeviceVerifyKey();
            var store = new ManifestStore(dir);
            var m7 = ManifestTestKit.BuildManifest(7, 1000);
            Assert.True(store.VerifyCandidate(m7, key).Accepted);
            store.Commit(m7, 1000);

            var same = store.VerifyCandidate(ManifestTestKit.BuildManifest(7, 1001), key);
            Assert.False(same.Accepted);
            Assert.Equal("revision_not_newer", same.Reason);

            var older = store.VerifyCandidate(ManifestTestKit.BuildManifest(6, 1002), key);
            Assert.False(older.Accepted);
            Assert.Equal("revision_not_newer", older.Reason);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Rejects_bad_signature_and_tampering_accepts_valid()
    {
        var dir = TestDirs.Create("manifest-sig");
        try
        {
            var key = ManifestTestKit.DeviceVerifyKey();
            var store = new ManifestStore(dir);

            var wrongKey = ManifestSigner.DeriveDeviceVerifyKey(new string('f', 64));
            var forged = ManifestTestKit.BuildManifest(3, 1000);
            forged.Signature = ManifestSigner.Sign(forged, wrongKey); // signed with the WRONG pin
            var verdict = store.VerifyCandidate(forged, key);
            Assert.False(verdict.Accepted);
            Assert.Equal("signature_invalid", verdict.Reason);

            var tampered = ManifestTestKit.BuildManifest(4, 1000);
            tampered.Occurrences.Add(ManifestTestKit.Bell("a", 5000, 9000)); // tamper AFTER signing
            var expectedOcc = tampered.Occurrences.Count;
            tampered.Signature = ManifestSigner.Sign(
                new ScheduleManifestPayload
                {
                    Revision = tampered.Revision, SchoolTz = tampered.SchoolTz,
                    GeneratedAt = tampered.GeneratedAt, HorizonStart = tampered.HorizonStart,
                    HorizonEnd = tampered.HorizonEnd, ExpiresAt = tampered.ExpiresAt,
                    Sounds = tampered.Sounds, Occurrences = [], // sign a payload WITHOUT the occurrence
                }, key);
            Assert.False(store.VerifyCandidate(tampered, key).Accepted);
            Assert.Equal(1, expectedOcc);

            var valid = ManifestTestKit.BuildManifest(5, 1000);
            Assert.True(store.VerifyCandidate(valid, key).Accepted);
            store.Commit(valid, 1000);
            Assert.Equal(5, store.LocalRevision);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Keeps_only_newest_accepted_and_survives_reload()
    {
        var dir = TestDirs.Create("manifest-reload");
        try
        {
            var key = ManifestTestKit.DeviceVerifyKey();
            var store = new ManifestStore(dir);
            store.Commit(ManifestTestKit.BuildManifest(8, 1000), 1000);
            store.Commit(ManifestTestKit.BuildManifest(9, 1001), 1001);

            var reloaded = new ManifestStore(dir);
            Assert.Equal(9, reloaded.LocalRevision);
            Assert.NotNull(reloaded.Current);
            Assert.Equal(9, reloaded.Current!.Revision);

            // A rejected candidate never replaces the accepted manifest.
            var wrongKey = ManifestSigner.DeriveDeviceVerifyKey(new string('e', 64));
            var bad = ManifestTestKit.BuildManifest(10, 1002);
            bad.Signature = ManifestSigner.Sign(bad, wrongKey);
            Assert.False(store.VerifyCandidate(bad, key).Accepted);
            Assert.Equal(9, store.LocalRevision);
        }
        finally { TestDirs.TryDelete(dir); }
    }
}

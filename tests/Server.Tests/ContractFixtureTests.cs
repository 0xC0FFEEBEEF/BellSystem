using System.Text.Json;
using SchoolBell.Contracts;

namespace Server.Tests;

/// Contract conformance: every frozen fixture must round-trip through the shared
/// Contracts library exactly as the server and clients will use it.
public class ContractFixtureTests
{
    static readonly string FixtureDir = FindRepoDir("contracts/fixtures");

    static string FindRepoDir(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, rel);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(rel);
    }

    [Fact]
    public void All_fixtures_parse_as_envelopes_and_round_trip()
    {
        var files = Directory.GetFiles(FixtureDir, "*.json");
        Assert.True(files.Length >= 17, $"expected >=17 fixtures, found {files.Length}");
        foreach (var f in files)
        {
            var env = ControlEnvelope.Parse(File.ReadAllText(f));
            Assert.NotNull(env);
            Assert.Equal(Protocol.Version, env!.V);
            Assert.NotNull(env.Type);
            // reparse our own re-serialization
            var again = ControlEnvelope.Parse(env.ToJson());
            Assert.NotNull(again);
            Assert.Equal(env.Type, again!.Type);
            Assert.Equal(env.EventId, again.EventId);
        }
    }

    [Theory]
    [InlineData("auth.challenge", typeof(AuthChallengePayload))]
    [InlineData("auth.response", typeof(AuthResponsePayload))]
    [InlineData("auth.ok", typeof(AuthOkPayload))]
    [InlineData("device.resync", typeof(DeviceResyncPayload))]
    [InlineData("device.status", typeof(DeviceStatusPayload))]
    [InlineData("schedule.manifest", typeof(ScheduleManifestPayload))]
    [InlineData("schedule.ack", typeof(ScheduleAckPayload))]
    [InlineData("manifest.rejected", typeof(ManifestRejectedPayload))]
    [InlineData("receipt", typeof(ReceiptPayload))]
    [InlineData("keepawake.set", typeof(KeepAwakeSetPayload))]
    [InlineData("keepawake.clear", typeof(KeepAwakeClearPayload))]
    [InlineData("keepawake.ack", typeof(KeepAwakeAckPayload))]
    [InlineData("announcement.offer", typeof(AnnouncementOfferPayload))]
    [InlineData("announcement.ready", typeof(AnnouncementReadyPayload))]
    [InlineData("announcement.reject", typeof(AnnouncementRejectPayload))]
    [InlineData("announcement.end", typeof(AnnouncementEndPayload))]
    [InlineData("media.token", typeof(MediaTokenPayload))]
    public void Fixture_payload_deserializes(string type, Type payloadType)
    {
        var env = ControlEnvelope.Parse(File.ReadAllText(Path.Combine(FixtureDir, type + ".json")));
        Assert.NotNull(env);
        var payload = JsonSerializer.Deserialize<JsonElement>(env!.Payload!.ToJsonString());
        var typed = payload.Deserialize(payloadType, ControlEnvelope.Json);
        Assert.NotNull(typed);
    }

    [Fact]
    public void Manifest_fixture_signature_round_trips_canonical_signing()
    {
        var env = ControlEnvelope.Parse(File.ReadAllText(Path.Combine(FixtureDir, "schedule.manifest.json")));
        var manifest = PayloadSerializer.ToPayload<ScheduleManifestPayload>(env!)!;
        var key = new byte[32]; new Random(42).NextBytes(key);
        manifest.Signature = ManifestSigner.Sign(manifest, key);
        Assert.True(ManifestSigner.Verify(manifest, key));

        // tampering with any occurrence must break the signature
        manifest.Occurrences[0] = manifest.Occurrences[0] with { StartsAt = manifest.Occurrences[0].StartsAt + 60_000 };
        Assert.False(ManifestSigner.Verify(manifest, key));
    }

    [Fact]
    public void Canonical_json_is_deterministic_and_sorted()
    {
        var a = CanonicalJson.Serialize(new { B = 1, A = new { D = 2, C = 3 } });
        var b = CanonicalJson.Serialize(new { A = new { C = 3, D = 2 }, B = 1 });
        Assert.Equal(a, b);
        Assert.True(a.IndexOf("\"a\"") < a.IndexOf("\"b\""));  // snake_case policy lowercases
        Assert.DoesNotContain(" ", a);
    }

    [Fact]
    public void Stable_occurrence_ids_survive_time_edits()
    {
        var id1 = OccurrenceIds.Compute(7, 2, "all", "2026-11-04", "start");
        var id2 = OccurrenceIds.Compute(7, 2, "all", "2026-11-04", "start");
        Assert.Equal(id1, id2);                                   // same event across revisions
        Assert.NotEqual(id1, OccurrenceIds.Compute(7, 2, "all", "2026-11-05", "start"));
        Assert.NotEqual(id1, OccurrenceIds.Compute(7, 2, "all", "2026-11-04", "end"));
    }

    [Fact]
    public void Media_frame_binary_fixture_parses_and_reencodes_identically()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "bin", "frame_sbmf.bin"));
        var frame = MediaFrame.TryParse(bytes);
        Assert.NotNull(frame);
        Assert.Equal(640, frame!.Value.Pcm.Length);   // 320 samples × 2 bytes
        Assert.Equal(bytes, frame.Value.ToBytes());
        Assert.False(frame.Value.IsLast);

        var last = new MediaFrame(1, 1793690000000, MediaFrame.FlagLast, new byte[640]);
        var parsed = MediaFrame.TryParse(last.ToBytes())!.Value;
        Assert.True(parsed.IsLast);

        var badMagic = new byte[24]; "XXXX"u8.CopyTo(badMagic);
        Assert.Null(MediaFrame.TryParse(badMagic));       // bad magic
        Assert.Null(MediaFrame.TryParse(new byte[10]));   // short
    }

    [Fact]
    public void Auth_proof_matches_across_producer_and_verifier()
    {
        const string secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var proof = AuthProof.Compute(secret, "nonce123", "device-1");
        Assert.True(AuthProof.Verify(secret, "nonce123", "device-1", proof));
        Assert.False(AuthProof.Verify(secret, "nonce123", "device-2", proof));
        Assert.False(AuthProof.Verify(secret, "nonce124", "device-1", proof));
    }

    [Fact]
    public void Oversized_control_message_is_rejected()
    {
        var big = ControlEnvelope.Create("device.status",
            PayloadSerializer.Node(new DeviceStatusPayload(
                "c", new string('x', 40_000), null, "ready", null, null, true,
                false, 0, false, 0, 0, 1, 0, new KeepAwakeStatus(false, false, 0), null, "running")));
        Assert.Null(ControlEnvelope.Parse(big.ToJson()));
    }
}

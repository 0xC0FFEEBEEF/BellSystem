using System.Security.Cryptography;
using System.Text;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

/// v1.1 client behaviour: ring now (claim, dedupe, expiry, suppression), device settings and
/// groups, tray history privacy, renderer jitter buffer, publisher signatures.
public class ExtensionClientTests
{
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private sealed class Run : IAsyncDisposable
    {
        public required FakeControlServer Server { get; init; }
        public required DeviceAgent Agent { get; init; }
        public required FakeBellPlayer Player { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Task { get; init; }
        public required string Dir { get; init; }
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Task; } catch { }
            await Server.DisposeAsync();
            TestDirs.TryDelete(Dir);
        }
    }

    private static async Task<Run> StartAsync()
    {
        var id = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var server = await FakeControlServer.StartAsync(id, secret);
        var dir = TestDirs.Create("ext");
        var player = new FakeBellPlayer();
        var agent = new DeviceAgent(AgentKit.MakeConfig(server.Url, dir, id, secret), TimeProvider.System, new HttpClient(),
            new CapturingLogger(), player, new FakePipeline(), new FakeRenderer(), new FakeIdentity(), new FakeKeepAwake());
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => agent.RunAsync(cts.Token));
        await server.WaitForAsync(Protocol.TDeviceResync);
        return new Run { Server = server, Agent = agent, Player = player, Cts = cts, Task = task, Dir = dir };
    }

    private static BellRingPayload Ring(string ringId, byte[] wav, long issuedAt, long expiresAt) =>
        new(ringId, "Office bell", new SoundRef("std-bell", Sha(wav), "/api/sounds/std-bell"), 3, "announcement", issuedAt, expiresAt, "office");

    private static bool HasReceipt(FakeControlServer s, string refId, string state)
    {
        lock (s.Received)
            return s.Received.Any(e => e.Type == Protocol.TReceipt &&
                PayloadSerializer.ToPayload<ReceiptPayload>(e) is { } r && r.OccurrenceOrAnnouncementId == refId && r.State == state);
    }

    [Fact]
    public async Task Ring_now_plays_once_even_when_the_same_ring_arrives_twice()
    {
        await using var run = await StartAsync();
        var wav = Encoding.UTF8.GetBytes("RIFF fake bell");
        run.Server.Sounds["std-bell"] = wav;
        var now = NowMs();
        var ring = Ring("ring-a", wav, now, now + 15_000);
        run.Server.QueueEvent(Protocol.TBellRing, PayloadSerializer.Node(ring));
        Assert.True(await run.Server.WaitUntilAsync(() => HasReceipt(run.Server, "ring-a", ReceiptState.Completed)));
        run.Server.QueueEvent(Protocol.TBellRing, PayloadSerializer.Node(ring));      // replay / duplicate
        await Task.Delay(1500);
        lock (run.Player.Requests) Assert.Single(run.Player.Requests);
        Assert.True(HasReceipt(run.Server, "ring-a", ReceiptState.Started));
    }

    [Fact]
    public async Task A_ring_that_arrives_after_its_expiry_is_recorded_expired_and_not_rung()
    {
        await using var run = await StartAsync();
        var wav = Encoding.UTF8.GetBytes("RIFF fake bell");
        run.Server.Sounds["std-bell"] = wav;
        var now = NowMs();
        run.Server.QueueEvent(Protocol.TBellRing, PayloadSerializer.Node(Ring("ring-late", wav, now - 60_000, now - 45_000)));
        Assert.True(await run.Server.WaitUntilAsync(() => HasReceipt(run.Server, "ring-late", ReceiptState.Expired)));
        lock (run.Player.Requests) Assert.Empty(run.Player.Requests);
    }

    [Fact]
    public async Task A_ring_whose_sound_fails_its_hash_check_is_reported_failed_and_not_played()
    {
        await using var run = await StartAsync();
        run.Server.Sounds["std-bell"] = Encoding.UTF8.GetBytes("tampered bytes");
        var now = NowMs();
        run.Server.QueueEvent(Protocol.TBellRing, PayloadSerializer.Node(Ring("ring-bad", Encoding.UTF8.GetBytes("expected bytes"), now, now + 15_000)));
        Assert.True(await run.Server.WaitUntilAsync(() => HasReceipt(run.Server, "ring-bad", ReceiptState.Failed)));
        lock (run.Player.Requests) Assert.Empty(run.Player.Requests);
    }

    [Fact]
    public async Task Device_settings_give_the_device_its_groups_so_group_bells_match_offline()
    {
        await using var run = await StartAsync();
        var groupBell = ManifestTestKit.Bell("grp-1", NowMs() + 3_600_000, NowMs() + 7 * 86_400_000, target: new TargetScope(null, [42], null));
        var manifest = ManifestTestKit.BuildManifest(1, NowMs(), [groupBell], soundSha: Sha(Encoding.UTF8.GetBytes("x")));
        run.Server.Sounds["std-bell"] = Encoding.UTF8.GetBytes("x");
        run.Server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(manifest));
        await run.Server.WaitForAsync(Protocol.TScheduleAck);
        Assert.Null(run.Agent.PeekNextOccurrence());                     // not in group 42 yet

        run.Server.QueueEvent(Protocol.TDeviceSettings, PayloadSerializer.Node(new DeviceSettingsPayload(5, "Speakers", 70, 90, [42], "Room 7")));
        for (int i = 0; i < 100 && run.Agent.PeekNextOccurrence() is null; i++) await Task.Delay(100);
        Assert.NotNull(run.Agent.PeekNextOccurrence());
        Assert.Equal(70, run.Agent.AnnouncementVolume);

        // an older settings version is ignored
        run.Server.QueueEvent(Protocol.TDeviceSettings, PayloadSerializer.Node(new DeviceSettingsPayload(4, null, 20, 90, [], null)));
        await Task.Delay(800);
        Assert.Equal("Speakers", run.Agent.Settings.Current!.OutputDevice);
        Assert.Contains(42L, run.Agent.EffectiveGroups);
    }

    [Fact]
    public async Task Targeted_history_never_keeps_text_and_is_dropped_when_the_user_changes()
    {
        await using var run = await StartAsync();
        run.Server.QueueEvent(Protocol.THistoryItem, PayloadSerializer.Node(new HistoryItemPayload("a1", NowMs(), "live", "Office — all", true, "done", "Buses are late.", false)));
        run.Server.QueueEvent(Protocol.THistoryItem, PayloadSerializer.Node(new HistoryItemPayload("a2", NowMs(), "live", "Office — 1 endpoint", false, "done", "Private note", false)));
        for (int i = 0; i < 100 && run.Agent.History.Snapshot().Count < 2; i++) await Task.Delay(100);
        Assert.Equal(2, run.Agent.History.Snapshot().Count);
        var items = run.Agent.History.Snapshot();
        Assert.Equal("Buses are late.", items.Single(i => i.AnnouncementId == "a1").Text);
        Assert.Null(items.Single(i => i.AnnouncementId == "a2").Text);
        run.Agent.OnActiveUserChanged();
        Assert.Equal(["a1"], run.Agent.History.Snapshot().Select(i => i.AnnouncementId));
    }

    // ------------------------------------------------------------------ renderer jitter buffer

    private static byte[] Pcm(int samples, short value)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++) { b[2 * i] = (byte)value; b[2 * i + 1] = (byte)(value >> 8); }
        return b;
    }

    [Fact]
    public void Jitter_buffer_waits_for_its_start_delay_then_plays()
    {
        var jb = new PcmJitterBuffer(16_000, startMs: 300, maxMs: 2000);
        var out1 = new float[320];
        jb.Write(Pcm(320 * 10, 1000));                // 200 ms: still priming
        Assert.Equal(320, jb.Read(out1));
        Assert.All(out1, v => Assert.Equal(0f, v));
        jb.Write(Pcm(320 * 5, 1000));                 // now 300 ms
        Assert.Equal(320, jb.Read(out1));
        Assert.All(out1, v => Assert.True(v > 0.02f));
    }

    [Fact]
    public void Jitter_buffer_pads_underruns_with_silence_and_rebuffers()
    {
        var jb = new PcmJitterBuffer(16_000, startMs: 200, maxMs: 2000);
        jb.Write(Pcm(3200, 1000));                     // exactly 200 ms
        var buf = new float[4000];
        Assert.Equal(4000, jb.Read(buf));            // 3200 real + 800 silence
        Assert.Equal(1, jb.Underruns);
        Assert.True(jb.Priming);
        Assert.Equal(0f, buf[3999]);
    }

    [Fact]
    public void Jitter_buffer_bounds_latency_by_dropping_the_oldest_audio()
    {
        var jb = new PcmJitterBuffer(16_000, startMs: 300, maxMs: 1000);
        jb.Write(Pcm(16_000 * 3, 500));              // 3 s burst
        Assert.True(jb.BufferedMs <= 1000);
        Assert.True(jb.DroppedSamples >= 32_000);
    }

    [Fact]
    public void Jitter_buffer_drains_after_end_then_reports_finished()
    {
        var jb = new PcmJitterBuffer(16_000, startMs: 300, maxMs: 2000);
        jb.Write(Pcm(1600, 800));                     // 100 ms, below the start delay
        jb.End();
        var buf = new float[3200];
        Assert.Equal(1600, jb.Read(buf));             // ended: plays what it has without waiting
        Assert.Equal(0, jb.Read(buf));
        Assert.True(jb.Finished);
    }

    // ------------------------------------------------------------------ publisher signatures

    [Fact]
    public void Release_manifest_signature_rejects_tampering_and_foreign_keys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = """{"commit":"abc","created_at":1,"files":{"windows/SchoolBellSetup.exe":{"bytes":3,"sha256":"aa"}},"product":"SchoolBell","version":"1.2.3"}""";
        var sig = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var parsed = ReleaseSignature.ParseVerified(manifest, sig, pem);
        Assert.NotNull(parsed);
        Assert.Equal("1.2.3", parsed!.Version);
        Assert.Equal("aa", parsed.Files["windows/SchoolBellSetup.exe"].Sha256);
        Assert.Null(ReleaseSignature.ParseVerified(manifest.Replace("1.2.3", "9.9.9"), sig, pem));
        Assert.Null(ReleaseSignature.ParseVerified(manifest, sig, other.ExportSubjectPublicKeyInfoPem()));
        Assert.Null(ReleaseSignature.ParseVerified(manifest, "not base64!", pem));
    }

    [Theory]
    [InlineData("0.9.1", "0.9.0", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("1.0.0", "1.0.0-rc1", 1)]
    [InlineData("1.0.0-rc1", "1.0.0-rc2", -1)]
    [InlineData("v2.0", "2.0.0", 0)]
    public void Version_ordering_for_updates(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ReleaseSignature.CompareVersions(a, b)));
}

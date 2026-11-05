using System.Security.Cryptography;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

/// Integration tests against an in-process FAKE control server (FakeControlServer)
/// that replicates the live server's handshake, outbox replay, manifest push, sound
/// delivery, keep-awake push and announcement/media flow. The live 127.0.0.1:5001
/// server is NOT contacted (enrollment needs an admin token; the fake is the harness).
public class FakeControlServerTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public required DeviceAgent Agent { get; init; }
        public required CapturingLogger Log { get; init; }
        public required FakeBellPlayer Player { get; init; }
        public required FakePipeline Pipeline { get; init; }
        public required FakeKeepAwake KeepAwake { get; init; }
        public required string Dir { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task AgentTask { get; init; }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await AgentTask; } catch (OperationCanceledException) { } catch { }
            Cts.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    private static Harness StartAgentAsync(FakeControlServer server, Action<ClaimLedger>? seedLedger = null)
    {
        // Use the fake server's enrolled device identity: enrollment itself is the installer's
        // job (POST /api/enroll needs a scoped token), so tests seed the same credentials
        // the installer would have written into config.json.
        var deviceId = server.DeviceId;
        var secret = server.DeviceSecretHex;
        var dir = TestDirs.Create("agent");
        if (seedLedger is not null) seedLedger(new ClaimLedger(dir));

        var cfg = AgentKit.MakeConfig(server.Url, dir, deviceId, secret);
        var log = new CapturingLogger();
        var player = new FakeBellPlayer();
        var pipeline = new FakePipeline();
        var keepAwake = new FakeKeepAwake();
        var agent = new DeviceAgent(cfg, TimeProvider.System, new HttpClient(), log,
            player, pipeline, new FakeRenderer(), new FakeIdentity(), keepAwake);
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => agent.RunAsync(cts.Token));
        return new Harness
        {
            Agent = agent, Log = log, Player = player, Pipeline = pipeline,
            KeepAwake = keepAwake, Dir = dir, Cts = cts, AgentTask = task,
        };
    }

    private static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static ScheduleManifestPayload BellManifest(long revision, long nowMs, string bellId, long startsAt, byte[] bellBytes)
    {
        var manifest = ManifestTestKit.BuildManifest(revision, nowMs,
            [ManifestTestKit.Bell(bellId, startsAt, nowMs + 7 * 86_400_000)],
            soundSha: Sha(bellBytes));
        return manifest;
    }

    // ------------------------------------------------------------------ //

    [Fact]
    public async Task Handshake_Resync_Replays_Missed_Manifest_And_Device_Acks()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL WAV BYTES");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();
        var manifestEvent = server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(
            BellManifest(3, now, "future-bell", now + 600_000, bell)));

        await using var h = StartAgentAsync(server);

        var ack = await server.WaitForAsync(Protocol.TScheduleAck);
        var ackPayload = PayloadSerializer.ToPayload<ScheduleAckPayload>(ack)!;
        Assert.Equal(3, ackPayload.Revision);
        Assert.True(server.ProofVerified);
        Assert.Single(server.Resyncs);
        Assert.Null(server.Resyncs[0].Cursor);
        Assert.Equal(0, server.Resyncs[0].CacheRevision);

        Assert.Equal(3, h.Agent.Manifests.LocalRevision);
        Assert.NotNull(h.Agent.Cache.GetSoundPath("std-bell"));
        Assert.Equal(manifestEvent.EventId, h.Agent.Cursor.Cursor); // durable cursor advanced past the manifest event

        // device.status reports the new cache revision quickly (on-change report).
        Assert.True(await server.WaitUntilAsync(
            () => server.Received.Any(e => e.Type == Protocol.TDeviceStatus &&
                PayloadSerializer.ToPayload<DeviceStatusPayload>(e)?.CacheRevision == 3), 10_000));
    }

    [Fact]
    public async Task Receipts_Survive_Forced_Disconnect_And_Reconnect()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        server.RudeCloseFirstConnections = 1; // first socket dies right after accept (server restart)

        // Crash-recovery receipts exist in the durable queue BEFORE any connection.
        await using var h = StartAgentAsync(server, ledger =>
        {
            for (int i = 1; i <= 3; i++) ledger.TryClaim($"crash-{i}", NowMs() - 10_000);
        });

        // Connection 1 is force-closed before resync can be sent; the receipts stay queued.
        // Connection 2 must carry them in its device.resync.
        // Connection 1 is force-closed before resync can be sent; the receipts stay queued.
        // Connection 2 must carry them in its device.resync.
        await server.WaitUntilAsync(() => server.ConnectionCount >= 2, 15_000);
        Assert.True(await server.WaitUntilAsync(
            () => server.Resyncs.Any(r => r.Receipts.Count(rc =>
                rc.State == ReceiptState.Unknown && rc.OccurrenceOrAnnouncementId.StartsWith("crash-")) == 3),
            15_000));
        Assert.True(server.ConnectionCount >= 2); // forced disconnect + reconnect really happened
        // No log line may contain the device secret.
        Assert.DoesNotContain(h.Log.Messages, m => m.Contains(secret));
    }

    [Fact]
    public async Task Durable_Cursor_Stops_Outbox_Redelivery_After_Reconnect()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();
        var manifestEvent = server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(
            BellManifest(4, now, "future", now + 600_000, bell)));

        await using var h = StartAgentAsync(server);
        await server.WaitForAsync(Protocol.TScheduleAck);
        var acksFirst = server.Received.Count(e => e.Type == Protocol.TScheduleAck);
        Assert.Equal(1, acksFirst);
        Assert.Equal(manifestEvent.EventId, h.Agent.Cursor.Cursor); // durable cursor at the processed event

        server.CloseLiveConnectionAbruptly(); // forced disconnect mid-session

        Assert.True(await server.WaitUntilAsync(() => server.Resyncs.Count >= 2, 15_000));
        var resync2 = server.Resyncs[^1];
        Assert.Equal(manifestEvent.EventId, resync2.Cursor);   // cursor survives reconnect
        Assert.Equal(4, resync2.CacheRevision);                 // cache revision survives too

        await Task.Delay(3_000); // give any (wrong) redelivery time to show up
        Assert.Equal(1, server.Received.Count(e => e.Type == Protocol.TScheduleAck)); // no re-ack, no duplicate bell
        Assert.Equal(4, h.Agent.Manifests.LocalRevision);
    }

    [Fact]
    public async Task Bell_Due_Plays_Completed_Receipt_And_Missing_Sound_Fails_Honestly()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();

        var manifest = ManifestTestKit.BuildManifest(5, now, [
            ManifestTestKit.Bell("due-soon", now + 1_500, now + 7 * 86_400_000),
            ManifestTestKit.Bell("no-sound", now + 2_500, now + 7 * 86_400_000, soundId: "ghost-sound"),
        ], soundSha: Sha(bell)); // manifest lists ONLY std-bell bytes; ghost-sound has no source
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(manifest));

        await using var h = StartAgentAsync(server);
        await server.WaitForAsync(Protocol.TScheduleAck);

        Assert.True(await server.WaitUntilAsync(
            () => AgentKit.CountReceipts(server.Received, "due-soon", ReceiptState.Completed) == 1, 15_000));
        Assert.True(await server.WaitUntilAsync(
            () => server.Received.Any(e => e.Type == Protocol.TReceipt &&
                AgentKit.ReceiptOf(e).OccurrenceOrAnnouncementId == "no-sound" &&
                AgentKit.ReceiptOf(e).State == ReceiptState.Failed &&
                AgentKit.ReceiptOf(e).ErrorCode == "sound_missing"), 15_000));

        var played = h.Player.Requests.Single(r => r.OccurrenceId == "due-soon");
        Assert.Equal("Period n", played.BellName);          // bell_name reaches the companion
        Assert.NotNull(played.WavPath);
        Assert.True(File.Exists(played.WavPath));
        Assert.Equal(75, played.FadeMs);
        Assert.Equal(0, server.Received.Count(e => e.Type == Protocol.TReceipt &&
            AgentKit.ReceiptOf(e).OccurrenceOrAnnouncementId == "no-sound" &&
            AgentKit.ReceiptOf(e).State == ReceiptState.Completed)); // never claims a playback it could not do
    }

    [Fact]
    public async Task KeepAwake_Set_And_Clear_Get_Acknowledged()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = new byte[1]; // single sound asset matching the manifest sha
        server.Sounds["std-bell"] = bell;
        var now = NowMs();
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(
            ManifestTestKit.BuildManifest(6, now, [], soundSha: Sha(bell))));

        await using var h = StartAgentAsync(server);
        await server.WaitForAsync(Protocol.TScheduleAck);

        server.QueueEvent(Protocol.TKeepAwakeSet, PayloadSerializer.Node(
            new KeepAwakeSetPayload("ka-1", now + 3_600_000, DisplayOff: false)));
        var ack = await server.WaitForAsync(Protocol.TKeepAwakeAck);
        var ackPayload = PayloadSerializer.ToPayload<KeepAwakeAckPayload>(ack)!;
        Assert.Equal("ka-1", ackPayload.PolicyId);
        Assert.True(ackPayload.Applied);
        Assert.True(h.KeepAwake.Applied);
        Assert.True(h.Agent.KeepAwakePolicies.Evaluate(NowMs()).Desired);

        server.QueueEvent(Protocol.TKeepAwakeClear, PayloadSerializer.Node(new KeepAwakeClearPayload("ka-1")));
        Assert.True(await server.WaitUntilAsync(
            () => server.Received.Count(e => e.Type == Protocol.TKeepAwakeAck) == 2, 10_000));
        Assert.Equal(1, h.KeepAwake.Clears);
        Assert.False(h.Agent.KeepAwakePolicies.Evaluate(NowMs()).Desired);
    }

    [Fact]
    public async Task Live_Announcement_Flows_And_Due_Bell_Is_Suppressed_Not_Late()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();

        var manifest = ManifestTestKit.BuildManifest(7, now, [
            ManifestTestKit.Bell("bell-late", now - 40_000, now + 7 * 86_400_000),   // past horizon
            ManifestTestKit.Bell("bell-live", now + 3_500, now + 7 * 86_400_000),    // lands mid-announcement
            ManifestTestKit.Bell("bell-far", now + 600_000, now + 7 * 86_400_000),
        ], soundSha: Sha(bell));
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(manifest));

        await using var h = StartAgentAsync(server);
        await server.WaitForAsync(Protocol.TScheduleAck);

        // expired receipt for the whole past bell arrives live on the control channel.
        Assert.True(await server.WaitUntilAsync(
            () => AgentKit.CountReceipts(server.Received, "bell-late", ReceiptState.Expired) == 1, 15_000));

        // Offer -> ready -> media token -> streaming frames with last flag -> completion.
        var announcementId = Guid.NewGuid().ToString();
        server.QueueEvent(Protocol.TAnnouncementOffer, PayloadSerializer.Node(new AnnouncementOfferPayload(
            announcementId, "live", Guid.NewGuid().ToString(), "live",
            new MediaFormat("pcm_s16le", 16000, 1, 20),
            now + 30_000, 300, Chime: true, "Office - all endpoints")));
        var ready = await server.WaitForAsync(Protocol.TAnnouncementReady);
        Assert.Equal(announcementId, PayloadSerializer.ToPayload<AnnouncementReadyPayload>(ready)!.AnnouncementId);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var frames = Enumerable.Range(0, 200).Select(i =>
            new MediaFrame((uint)i, (ulong)(now + i * 20), i == 199 ? MediaFrame.FlagLast : (byte)0, new byte[640])).ToList();
        server.Media = new FakeControlServer.MediaPlan(announcementId, token, frames, PacingMs: 20);
        server.QueueEvent(Protocol.TMediaToken, PayloadSerializer.Node(new MediaTokenPayload(announcementId, token, now + 30_000)));

        // The bell due mid-announcement must be suppressed and must NOT be played.
        Assert.True(await server.WaitUntilAsync(
            () => AgentKit.CountReceipts(server.Received, "bell-live", ReceiptState.Suppressed) == 1, 15_000));
        Assert.DoesNotContain(h.Player.Requests, r => r.OccurrenceId == "bell-live");
        Assert.DoesNotContain(h.Player.Requests, r => r.OccurrenceId == "bell-late");

        // Media chain: started -> many frames through the jitter buffer -> last flag -> completed.
        Assert.True(await server.WaitUntilAsync(
            () => AgentKit.CountReceipts(server.Received, announcementId, ReceiptState.Started) == 1, 15_000));
        Assert.True(await server.WaitUntilAsync(
            () => AgentKit.CountReceipts(server.Received, announcementId, ReceiptState.Completed) == 1, 25_000));

        Assert.Single(h.Pipeline.Starts);
        Assert.Equal(announcementId, h.Pipeline.Starts[0].AnnouncementId);
        Assert.Equal(80, h.Pipeline.Starts[0].PolicyVolume);   // ann.start carries policy_volume explicitly
        Assert.True(h.Pipeline.Starts[0].Chime);
        Assert.True(h.Pipeline.Frames.Count >= 190, $"got {h.Pipeline.Frames.Count} frames");
        Assert.Contains(h.Pipeline.Ends, e => e.Id == announcementId);

        // Token and secret must never appear in logs.
        Assert.DoesNotContain(h.Log.Messages, m => m.Contains(token) || m.Contains(secret));
    }

    [Fact]
    public async Task Manifest_Bad_Signature_And_Old_Revision_Are_Rejected_And_Reported()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();

        var forged = ManifestTestKit.BuildManifest(1, now, [ManifestTestKit.Bell("bad", now + 60_000, now + 7 * 86_400_000)], Sha(bell));
        forged.Signature = ManifestSigner.Sign(forged, ManifestSigner.DeriveDeviceVerifyKey("ab".PadRight(64, 'a'))); // wrong pin
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(forged));

        await using var h = StartAgentAsync(server);

        var rejected = await server.WaitForAsync(Protocol.TManifestRejected);
        var rej = PayloadSerializer.ToPayload<ManifestRejectedPayload>(rejected)!;
        Assert.Equal(1, rej.Revision);
        Assert.Equal("signature_invalid", rej.Reason);
        Assert.Equal(0, h.Agent.Manifests.LocalRevision); // rejected manifest is NEVER used
        Assert.Empty(h.Player.Requests);

        var valid = BellManifest(2, now, "ok-bell", now + 60_000, bell);
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(valid));
        await server.WaitForAsync(Protocol.TScheduleAck);
        Assert.Equal(2, h.Agent.Manifests.LocalRevision);

        // An old revision re-sent as a new event is rejected with the revision reason.
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(BellManifest(2, now, "dup", now + 60_000, bell)));
        Assert.True(await server.WaitUntilAsync(
            () => server.Received.Any(e => e.Type == Protocol.TManifestRejected &&
                PayloadSerializer.ToPayload<ManifestRejectedPayload>(e)?.Reason == "revision_not_newer"), 10_000));
        Assert.DoesNotContain(h.Player.Requests, r => r.OccurrenceId == "bad");
    }

    [Fact]
    public async Task Large_Manifest_Over_32KB_With_Multibyte_Names_Is_Accepted_And_Client_Advertises_Its_Limit()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL WAV BYTES");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();
        // a real school week: 400 bells; accented names so multi-byte characters straddle receive chunks
        var occurrences = Enumerable.Range(0, 400).Select(i =>
            new Occurrence($"occ-{i:D4}-{new string('a', 52)}", now + 600_000 + i * 60_000, $"Récréation période {i} ✓",
                "start", "std-bell", "announcement", 3, new TargetScope(All: true, null, null),
                "High School", "2026-11-05", "08:30", now + 7 * 86_400_000)).ToList();
        var manifest = ManifestTestKit.BuildManifest(9, now, occurrences, soundSha: Sha(bell));
        var size = System.Text.Encoding.UTF8.GetByteCount(
            ControlEnvelope.Create(Protocol.TScheduleManifest, PayloadSerializer.Node(manifest)).ToJson());
        Assert.True(size > 4 * Protocol.MaxControlMessageBytes, $"test manifest is only {size} bytes");
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(manifest));

        await using var h = StartAgentAsync(server);

        var ack = await server.WaitForAsync(Protocol.TScheduleAck);
        Assert.Equal(9, PayloadSerializer.ToPayload<ScheduleAckPayload>(ack)!.Revision);
        Assert.Equal(9, h.Agent.Manifests.LocalRevision);
        Assert.Equal(1, server.ConnectionCount);   // never dropped the connection over the size
        Assert.Equal(Protocol.MaxServerMessageBytes, server.LastAuthResponse!.MaxMessageBytes);
    }

    [Fact]
    public async Task Bells_Due_At_The_Same_Moment_Ring_Once_And_Each_Records_The_Result()
    {
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();
        var at = now + 1_500;
        // e.g. an "All" schedule and a group schedule that both include this computer
        var hs = ManifestTestKit.Bell("hs-pass", at, now + 7 * 86_400_000) with { BellName = "Pass", Kind = "end" };
        var jh = ManifestTestKit.Bell("jh-6th", at, now + 7 * 86_400_000) with { BellName = "6th period", Kind = "start" };
        var later = ManifestTestKit.Bell("later", at + 1_000, now + 7 * 86_400_000);
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(
            ManifestTestKit.BuildManifest(6, now, [hs, jh, later], soundSha: Sha(bell))));

        await using var h = StartAgentAsync(server);
        Assert.True(await server.WaitUntilAsync(
            () => AgentKit.CountReceipts(server.Received, "hs-pass", ReceiptState.Completed) == 1 &&
                  AgentKit.CountReceipts(server.Received, "jh-6th", ReceiptState.Completed) == 1 &&
                  AgentKit.CountReceipts(server.Received, "later", ReceiptState.Completed) == 1, 15_000));

        lock (h.Player.Requests)
        {
            Assert.Equal(2, h.Player.Requests.Count);                     // one for the shared moment, one later
            Assert.Equal("jh-6th", h.Player.Requests[0].OccurrenceId);    // the starting period represents it
            Assert.Equal("later", h.Player.Requests[1].OccurrenceId);
        }
    }

    [Fact]
    public async Task A_Bell_Minutes_After_One_That_Played_Still_Rings_On_Time()
    {
        // field bug: 3-min pass bells expired on most computers because the loop slept from a clock
        // reading taken before the previous 3-second bell played, waking past the 2 s grace
        var deviceId = Guid.NewGuid().ToString();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var server = await FakeControlServer.StartAsync(deviceId, secret);
        var bell = System.Text.Encoding.UTF8.GetBytes("BELL");
        server.Sounds["std-bell"] = bell;
        var now = NowMs();
        server.QueueEvent(Protocol.TScheduleManifest, PayloadSerializer.Node(ManifestTestKit.BuildManifest(8, now, [
            ManifestTestKit.Bell("block-ends", now + 2_000, now + 7 * 86_400_000),
            ManifestTestKit.Bell("pass-ends", now + 8_000, now + 7 * 86_400_000),
        ], soundSha: Sha(bell))));

        await using var h = StartAgentAsync(server);
        h.Player.PlayMs = 3_500;                                   // a 3 s bell plus fade and pipe
        Assert.True(await server.WaitUntilAsync(
            () => AgentKit.CountReceipts(server.Received, "block-ends", ReceiptState.Completed) == 1 &&
                  (AgentKit.CountReceipts(server.Received, "pass-ends", ReceiptState.Completed) == 1 ||
                   AgentKit.CountReceipts(server.Received, "pass-ends", ReceiptState.Expired) == 1), 20_000));
        Assert.Equal(0, AgentKit.CountReceipts(server.Received, "pass-ends", ReceiptState.Expired));
        Assert.Equal(1, AgentKit.CountReceipts(server.Received, "pass-ends", ReceiptState.Completed));
    }
}

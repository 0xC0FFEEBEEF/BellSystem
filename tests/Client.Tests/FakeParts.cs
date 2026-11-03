using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public sealed class FakeBellPlayer : IBellPlayer
{
    public List<PipeBellPlay> Requests { get; } = [];
    public bool Played { get; set; } = true;
    public Task<BellPlayOutcome> PlayAsync(PipeBellPlay play, CancellationToken ct)
    {
        lock (Requests) Requests.Add(play);
        return Task.FromResult(new BellPlayOutcome(Played, Played ? null : "renderer"));
    }
}

public sealed class FakePipeline : IAnnouncementPipeline
{
    public List<PipeAnnStart> Starts { get; } = [];
    public List<MediaFrame> Frames { get; } = [];
    public List<(string Id, string EndedBy)> Ends { get; } = [];

    public Task StartAsync(PipeAnnStart start, CancellationToken ct)
    {
        lock (Starts) Starts.Add(start);
        return Task.CompletedTask;
    }
    public Task FramesAsync(string announcementId, IReadOnlyList<MediaFrame> frames, CancellationToken ct)
    {
        lock (Frames) Frames.AddRange(frames);
        return Task.CompletedTask;
    }
    public Task EndAsync(string announcementId, string endedBy, CancellationToken ct)
    {
        lock (Ends) Ends.Add((announcementId, endedBy));
        return Task.CompletedTask;
    }
}

public sealed class FakeRenderer : IRendererProvider
{
    public RendererInfo Info { get; set; } = new("ready", null, "Speakers (Fake)", true, false);
    public VolumeSnapshot Volumes { get; set; } = new(false, 42, false, 55);
    public string CompanionState { get; set; } = "running";
    public RendererInfo GetRenderer() => Info;
    public VolumeSnapshot GetVolumes() => Volumes;
}

public sealed class FakeIdentity : IIdentityProvider
{
    public (string? User, string? Sid) GetActiveUser() => ("james.doe", "S-1-5-21-1");
}

public sealed class FakeKeepAwake : IKeepAwakeApplier
{
    public List<bool> Applies { get; } = [];
    public int Clears { get; private set; }
    public bool Applied { get; private set; }
    public Task<bool> RequestApplyAsync(bool displayOff, CancellationToken ct)
    {
        lock (Applies) Applies.Add(displayOff);
        Applied = true;
        return Task.FromResult(true);
    }
    public Task RequestClearAsync(CancellationToken ct)
    {
        Clears++;
        Applied = false;
        return Task.CompletedTask;
    }
}

public static class AgentKit
{
    public static ClientConfig MakeConfig(string serverUrl, string dataDir, string deviceId, string secretHex) =>
        new()
        {
            ServerUrl = serverUrl,
            DeviceId = deviceId,
            DeviceSecret = secretHex,
            ServerTrustPin = ManifestTestKit.TrustPinHex,
            DataDir = dataDir,
            ClientVersion = "test",
            StatusReportSeconds = 5, // test-friendly cadence (production default remains 60)
            MissedBellGraceSeconds = 2,
            ReconnectCapSeconds = 2, // keep integration tests fast
        };

    public static ReceiptPayload ReceiptOf(ControlEnvelope env) =>
        PayloadSerializer.ToPayload<ReceiptPayload>(env)!;

    public static int CountReceipts(List<ControlEnvelope> received, string id, string state) =>
        received.Count(e => e.Type == Protocol.TReceipt &&
                            ReceiptOf(e).OccurrenceOrAnnouncementId == id &&
                            ReceiptOf(e).State == state);
}

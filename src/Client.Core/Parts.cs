using SchoolBell.Contracts;
namespace SchoolBell.Client.Core;

public sealed record BellPlayOutcome(bool Played, string? Reason);

/// Playback instruction sink (bell): on Windows implemented over the named pipe to the
/// companion; in tests a fake. The claim is already durable before this is called.
public interface IBellPlayer
{
    Task<BellPlayOutcome> PlayAsync(PipeBellPlay play, CancellationToken ct);
}

/// Live/recorded announcement rendering pipeline (companion side).
public interface IAnnouncementPipeline
{
    Task StartAsync(PipeAnnStart start, CancellationToken ct);
    Task FramesAsync(string announcementId, IReadOnlyList<SchoolBell.Contracts.MediaFrame> frames, CancellationToken ct);
    Task EndAsync(string announcementId, string endedBy, CancellationToken ct);
}

/// Renderer + volume facts as reported by the companion (via the pipe), used for
/// offer decisions and device.status.
public interface IRendererProvider
{
    RendererInfo GetRenderer();
    VolumeSnapshot GetVolumes();
    string CompanionState { get; } // running | hidden | missing
    IReadOnlyList<string> AvailableOutputs => [];
    string RendererOwner => "companion";
}

public interface IIdentityProvider
{
    (string? User, string? Sid) GetActiveUser();
}

/// Keep-awake application is marshalled to a dedicated service thread (apply AND clear
/// must happen on the same thread). Implementations must also re-evaluate the policy
/// store periodically so expiry releases the state even while offline.
public interface IKeepAwakeApplier
{
    Task<bool> RequestApplyAsync(bool displayOff, CancellationToken ct);
    Task RequestClearAsync(CancellationToken ct);
    bool Applied { get; }
}

/// Client update installer (Windows: verifies the publisher signature, downloads, runs the
/// signed installer through the rollback-capable update runner). Reports progress through
/// the callback, which the agent forwards as update.status.
public interface IClientUpdater
{
    string CurrentVersion { get; }
    Task HandleOfferAsync(UpdateOfferPayload offer, Func<UpdateStatusPayload, Task> report, CancellationToken ct);
    /// Result of an update that finished while the service was being replaced (read once at start).
    UpdateStatusPayload? TakeCompletedResult();
}

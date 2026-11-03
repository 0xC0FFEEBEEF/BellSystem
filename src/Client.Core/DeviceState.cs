using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public sealed record RendererInfo(
    string State,                 // ready | no_renderer | error
    string? Reason,               // signed_out | missing_output | crash | renderer_slot_taken | service_offline
    string? OutputDevice,
    bool OutputPresent,
    bool VolumeLocked);

public sealed record VolumeSnapshot(
    bool MasterMuted, int MasterVolume, bool SessionMuted, int SessionVolume);

/// Decision table for announcement offers (PROTOCOL §7): the device replies
/// announcement.ready only when its renderer is ready, an output device is present
/// and the volume override is available; otherwise announcement.reject with a reason.
public static class AnnouncementGate
{
    public const string ReasonRenderer = "renderer";
    public const string ReasonOutput = "output";
    public const string ReasonVolumeLocked = "volume_locked";
    // Extension beyond the §7 reason enum (reported as a protocol ambiguity):
    public const string ReasonOfferExpired = "offer_expired";

    public static (bool Ready, string? Reason) Decide(AnnouncementOfferPayload offer, long nowMs, RendererInfo renderer)
    {
        if (offer.OfferExpiresAt <= nowMs) return (false, ReasonOfferExpired);
        if (renderer.State != "ready") return (false, ReasonRenderer);
        if (!renderer.OutputPresent || string.IsNullOrEmpty(renderer.OutputDevice)) return (false, ReasonOutput);
        if (renderer.VolumeLocked) return (false, ReasonVolumeLocked);
        return (true, null);
    }
}

/// Builds the device.status payload (PROTOCOL §4) from live component state.
public sealed record StatusFacts(
    string ComputerName,
    string? ActiveUser,
    string? ActiveUserSid,
    RendererInfo Renderer,
    VolumeSnapshot Volumes,
    KeepAwakeState KeepAwake,
    bool KeepAwakeApplied,
    PlaybackResult? LastPlayback,
    string Companion,             // running | hidden | missing
    List<string>? AvailableOutputs = null,
    string? RendererOwner = null);

public sealed class StatusComposer
{
    private readonly BellClock _clock;
    private readonly ManifestStore _manifests;

    public StatusComposer(BellClock clock, ManifestStore manifests)
    {
        _clock = clock;
        _manifests = manifests;
    }

    public DeviceStatusPayload Build(StatusFacts facts, long cacheExpiresAt) =>
        new(
            facts.ComputerName, facts.ActiveUser, facts.ActiveUserSid,
            facts.Renderer.State, facts.Renderer.Reason,
            facts.Renderer.OutputDevice, facts.Renderer.OutputPresent,
            facts.Volumes.MasterMuted, facts.Volumes.MasterVolume,
            facts.Volumes.SessionMuted, facts.Volumes.SessionVolume,
            _clock.OffsetMs(), _manifests.LocalRevision, cacheExpiresAt,
            new KeepAwakeStatus(facts.KeepAwake.Desired, facts.KeepAwakeApplied, facts.KeepAwake.MaxExpiresAt),
            facts.LastPlayback, facts.Companion, facts.AvailableOutputs, facts.RendererOwner);
}

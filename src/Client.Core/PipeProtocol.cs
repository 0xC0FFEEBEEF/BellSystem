using System.Text.Json;

namespace SchoolBell.Client.Core;

/// Companion<->service local pipe protocol v1 (see PIPE.md for the full spec).
/// JSON lines, snake_case (Contracts options). The named pipe is local-only and
/// authenticated by DACL; the companion never accepts arbitrary commands or paths.
public static class PipeProtocol
{
    public const string PipeName = "schoolbell-v1";        // \\.\pipe\schoolbell-v1
    public const int ProtocolVersion = 1;
    public const int MaxLineBytes = 64 * 1024;             // generous batch headroom (ann.frames)
    public const int MaxFramesPerBatch = 50;               // 50 * (20+640) -> base64 fits MaxLineBytes
    public const int CompanionHeartbeatMs = 3_000;         // companion pings every 3 s
    public const int RendererSlotReclaimMs = 12_000;       // service reclaims slot after 12 s silence

    // companion -> service
    public const string THello = "hello";
    public const string THeartbeat = "heartbeat";
    public const string TStatusReport = "status.report";
    public const string TBellResult = "bell.result";
    public const string TVolumeRestore = "ann.volume_restore";

    // service -> companion
    public const string THelloOk = "hello.ok";
    public const string TBellPlay = "bell.play";
    public const string TAnnStart = "ann.start";
    public const string TAnnFrames = "ann.frames";
    public const string TAnnEnd = "ann.end";
    public const string TAnnTranscript = "ann.transcript";
    public const string TTranscriptNotify = "transcript.notify";
    public const string TKeepAwakeState = "keepawake.state";
    public const string TManifestState = "manifest.state";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string SerializeLine(string type, object payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, Json);
        var line = JsonSerializer.Serialize(new PipeMessage(type, element), Json);
        if (System.Text.Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
            throw new InvalidOperationException($"pipe line exceeds {MaxLineBytes} bytes");
        return line;
    }

    public static PipeMessage? ParseLine(string line)
    {
        if (line.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(line) > MaxLineBytes) return null;
        try { return JsonSerializer.Deserialize<PipeMessage>(line, Json); }
        catch (JsonException) { return null; }
    }
}

public sealed record PipeMessage(string Type, JsonElement? Payload)
{
    public T? PayloadAs<T>() => Payload is null ? default : Payload.Value.Deserialize<T>(PipeProtocol.Json);
}

// ---------- companion -> service ----------
public sealed record PipeHello(string Slot, string ClientVersion);         // slot: renderer | status_only
public sealed record PipeStatusReport(
    string Renderer, string? RendererReason,
    string? OutputDevice, bool OutputPresent,
    bool MasterMuted, int MasterVolume, bool SessionMuted, int SessionVolume,
    string CompanionState);                                                 // running | hidden

public sealed record PipeBellResult(string OccurrenceId, string Result, string? Reason); // played | failed
public sealed record PipeVolumeRestore(string AnnouncementId);

// ---------- service -> companion ----------
public sealed record PipeHelloOk(
    string SlotGranted, string CacheDir, int AnnouncementVolume, int BellFadeMs, string ComputerName);
public sealed record PipeBellPlay(
    string OccurrenceId, string BellName, string WavPath, int DurationS, int FadeMs,
    string VolumePolicy, int PolicyVolume);
public sealed record PipeAnnStart(
    string AnnouncementId, string Kind, string SourceLabel, bool Chime,
    string VolumePolicy, int PolicyVolume, int MaxDurationS);
public sealed record PipeFrameLine(uint Seq, ulong CaptureTsMs, byte Flags, string PcmB64);
public sealed record PipeAnnFrames(string AnnouncementId, List<PipeFrameLine> Frames);
public sealed record PipeAnnEnd(string AnnouncementId, string EndedBy);
public sealed record PipeAnnTranscript(
    string AnnouncementId, string Status, bool Schoolwide, string? Text, long At);
public sealed record PipeKeepAwakeState(bool Desired, bool DisplayOff, long ExpiresAt);
public sealed record PipeNextOccurrence(string OccurrenceId, string BellName, long StartsAt, string LocalHm);
public sealed record PipeManifestState(long Revision, long ExpiresAt, bool Stale, PipeNextOccurrence? Next);

// ---------- v1.1 additions (renderer arbitration, settings, history, volume recovery) ----------

/// Volume state captured before an override. Persisted by the service so the next renderer to
/// start (any user, or the service's own signed-out renderer) can restore it after a crash.
/// Restores apply only to the endpoint with the same DeviceId, never to a different speaker.
public sealed record VolumeRecoveryRecord(
    string DeviceId, string DeviceName, bool MasterMuted, float MasterScalar,
    bool SessionMuted, float SessionVolume, long CapturedAt, string Owner);

public sealed record PipeVolumeOverride(VolumeRecoveryRecord? Record);          // companion -> service
public sealed record PipeVolumeRecover(VolumeRecoveryRecord Record);            // service -> companion
public sealed record PipeSettings(string? OutputDevice, int AnnouncementVolume, int VolumeCap, int BellNotificationSeconds = 12);
public sealed record PipeHistoryList(List<SchoolBell.Contracts.HistoryItemPayload> Items);
public sealed record PipeServiceState(
    bool Connected, string ServerUrl, string RendererOwner, string ComputerName, string ClientVersion,
    string? UpdateState);

public static class PipeTypes
{
    public const string VolumeOverride = "volume.override";   // companion -> service
    public const string HistoryGet = "history.get";           // companion -> service
    public const string Settings = "settings";                // service -> companion
    public const string VolumeRecover = "volume.recover";     // service -> companion
    public const string HistoryList = "history.list";         // service -> companion
    public const string ServiceState = "service.state";       // service -> companion
}

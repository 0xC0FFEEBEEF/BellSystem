using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchoolBell.Contracts;

/// Control envelope shared by server, clients and simulator (PROTOCOL.md §3).
public sealed record ControlEnvelope(
    int V,
    string Type,
    string? EventId = null,
    long? IssuedAt = null,
    JsonNode? Payload = null)
{
    public static ControlEnvelope Create(string type, JsonNode? payload = null) =>
        new(Protocol.Version, type, Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ControlEnvelope? Parse(string json, int maxBytes = Protocol.MaxControlMessageBytes)
    {
        if (json.Length > maxBytes) return null;
        try
        {
            var env = JsonSerializer.Deserialize<ControlEnvelope>(json, Json);
            if (env is null || env.V != Protocol.Version || string.IsNullOrEmpty(env.Type)) return null;
            return env;
        }
        catch (JsonException) { return null; }
    }
}

public static class PayloadSerializer
{
    public static T? ToPayload<T>(ControlEnvelope env) =>
        env.Payload is null ? default : env.Payload.Deserialize<T>(ControlEnvelope.Json);

    public static JsonNode Node<T>(T payload) => JsonSerializer.SerializeToNode(payload, ControlEnvelope.Json)!;
}

// ---------- payload records (field names serialize snake_case) ----------

public sealed record AuthChallengePayload(string Nonce);
/// MaxMessageBytes (added in 0.9.5): the largest server message this device accepts. Absent on
/// older clients, which the server then holds to Protocol.MaxControlMessageBytes.
public sealed record AuthResponsePayload(string DeviceId, string ClientVersion, string Proof,
    int? MaxMessageBytes = null, List<string>? Capabilities = null);
public sealed record AuthOkPayload(long ServerTime);

public sealed record QueuedReceipt(string OccurrenceOrAnnouncementId, string State, long At, string? ErrorCode);
public sealed record DeviceResyncPayload(string? Cursor, long CacheRevision, List<QueuedReceipt> Receipts);

public sealed record PlaybackResult(string OccurrenceOrAnnouncementId, string State, long At);
public sealed record KeepAwakeStatus(bool Desired, bool Applied, long ExpiresAt);
public sealed record DeviceStatusPayload(
    string ComputerName,
    string? ActiveUser,
    string? ActiveUserSid,
    string Renderer,               // ready | no_renderer | error
    string? RendererReason,        // signed_out | missing_output | crash | ...
    string? OutputDevice,
    bool OutputPresent,
    bool MasterMuted, int MasterVolume,
    bool SessionMuted, int SessionVolume,
    long ClockOffsetMs,
    long CacheRevision,
    long CacheExpiresAt,
    KeepAwakeStatus KeepAwake,
    PlaybackResult? LastPlayback,
    string Companion,              // running | hidden | missing
    List<string>? AvailableOutputs = null,   // v1.1: speakers present, for the /admin output chooser
    string? RendererOwner = null);           // v1.1: companion | service

public sealed record SoundRef(string Id, string Sha256, string Url);

public sealed record TargetScope(bool? All, List<long>? Groups, List<string>? Devices)
{
    public bool Matches(string deviceId, IEnumerable<long> deviceGroups)
    {
        if (All == true) return true;
        if (Devices is not null && Devices.Contains(deviceId)) return true;
        if (Groups is not null && Groups.Any(g => deviceGroups.Contains(g))) return true;
        return false;
    }
}

public sealed record Occurrence(
    string OccurrenceId,
    long StartsAt,
    string BellName,
    string Kind,                   // start | end | warn
    string SoundId,
    string VolumePolicy,           // announcement | inherit
    int DurationS,
    TargetScope Target,
    string ScheduleTemplate,
    string LocalDate,
    string LocalHm,
    long ExpiresAt);

public sealed class ScheduleManifestPayload
{
    public long Revision { get; set; }
    public string SchoolTz { get; set; } = "";
    public long GeneratedAt { get; set; }
    public long HorizonStart { get; set; }
    public long HorizonEnd { get; set; }
    public long ExpiresAt { get; set; }
    public List<SoundRef> Sounds { get; set; } = [];
    public List<Occurrence> Occurrences { get; set; } = [];
    public string Signature { get; set; } = "";  // base64 HMAC over CanonicalJson (Signature excluded)
}

public sealed record ScheduleAckPayload(long Revision, long AcceptedAt, long ExpiresAt);
public sealed record ManifestRejectedPayload(long Revision, string Reason);

public sealed record ReceiptPayload(string OccurrenceOrAnnouncementId, string State, long At, string? ErrorCode);

public sealed record KeepAwakeSetPayload(string PolicyId, long ExpiresAt, bool DisplayOff);
public sealed record KeepAwakeClearPayload(string PolicyId);
public sealed record KeepAwakeAckPayload(string PolicyId, long AppliedAt, bool Applied);

public sealed record MediaFormat(string Codec, int SampleRate, int Channels, int FrameMs);
public sealed record AnnouncementOfferPayload(
    string AnnouncementId,
    string Kind,                   // live | recorded
    string AudienceSnapshotId,
    string Priority,               // live | normal
    MediaFormat Format,
    long OfferExpiresAt,
    int MaxDurationS,
    bool Chime,
    string SourceLabel);
public sealed record AnnouncementReadyPayload(string AnnouncementId, string OutputDevice);
public sealed record AnnouncementRejectPayload(string AnnouncementId, string Reason);
public sealed record AnnouncementEndPayload(string AnnouncementId, string EndedBy, long At);
public sealed record MediaTokenPayload(string AnnouncementId, string Token, long ExpiresAt);

// ---------- v1 additive extensions (PROTOCOL.md §13) ----------

/// Immediate bell ("ring now"). The ring id doubles as the receipt id; it is claimed in the
/// device ledger like a scheduled occurrence, so a replayed ring never plays twice, and a ring
/// that arrives after expires_at is recorded expired instead of ringing late.
public sealed record BellRingPayload(
    string RingId, string BellName, SoundRef Sound, int DurationS, string VolumePolicy,
    long IssuedAt, long ExpiresAt, string RequestedBy);

/// Per-device settings chosen in /admin. `Groups` lets the device match group-scoped bells in
/// the cached manifest. `OutputDevice` null = the Windows default output (documented); a named
/// output that disappears is reported missing_output, never silently replaced.
public sealed record DeviceSettingsPayload(
    long SettingsVersion, string? OutputDevice, int AnnouncementVolume, int VolumeCap,
    List<long> Groups, string? Room);

/// Tray history entry. Text is present only for schoolwide announcements; targeted
/// announcements carry no text (office-only until verified identity integration exists).
public sealed record HistoryItemPayload(
    string AnnouncementId, long At, string Kind, string SourceLabel, bool Schoolwide,
    string TranscriptStatus, string? Text, bool Corrected);

/// Ephemeral live transcript update. It is sent only to the frozen audience while the
/// announcement is active and is not placed in the durable outbox. Text is present only for
/// schoolwide announcements; targeted transcript text remains office-only.
public sealed record AnnouncementTranscriptPayload(
    string AnnouncementId, string Status, bool Schoolwide, string? Text, long At);

/// Signed client update. `ReleaseManifest` is the exact release-manifest.json text and
/// `ReleaseSignature` its base64 DER ECDSA-P256-SHA256 signature by the publisher key pinned at
/// install time. The device trusts nothing else in this message: it checks that the signed
/// manifest's version matches and that the downloaded file's sha256 matches the manifest entry.
public sealed record UpdateOfferPayload(
    string Version, string ArtifactPath, string Url, long Bytes, string ReleaseManifest,
    string ReleaseSignature, bool AllowDowngrade, long NotBefore);

/// state: received | downloading | verified | deferred | installing | installed | failed | rolled_back
public sealed record UpdateStatusPayload(string Version, string State, string? Error, long At);

/// release-manifest.json (signed by the publisher key; see build/release.sh).
public sealed record ReleaseManifest(
    string Product, string Version, string Commit, long CreatedAt,
    Dictionary<string, ReleaseFile> Files);
public sealed record ReleaseFile(string Sha256, long Bytes);

namespace SchoolBell.Contracts;

/// Protocol-wide constants (PROTOCOL.md v1). Bounds are configurable defaults,
/// never device-count ceilings.
public static class Protocol
{
    public const int Version = 1;

    public const int MaxControlMessageBytes = 32 * 1024;
    public const int MaxMediaFrameBytes = 1024;
    public const int MaxMediaPayloadBytes = 640;          // 320 samples @16kHz mono s16le
    public const int FrameMs = 20;
    public const int SampleRate = 16000;

    public const int HeartbeatSeconds = 10;
    public const int StaleAfterSeconds = 30;
    public const int ReconnectCapSeconds = 30;

    public const int MissedBellGraceSecondsDefault = 2;
    public const int ManifestHorizonDays = 7;
    public const int TalkLeaseMaxSecondsDefault = 300;
    public const int JitterBufferStartMs = 300;
    public const int JitterBufferMaxMs = 2000;
    public const int PerDeviceSendQueueFrames = 1500;     // ~30 s of live audio
    public const int ReceiptQueueMax = 5000;
    public const int ResyncReceiptsMax = 500;

    public const string MediaMagic = "SBMF";

    // control message types
    public const string TAuthChallenge = "auth.challenge";
    public const string TAuthResponse = "auth.response";
    public const string TAuthOk = "auth.ok";
    public const string TDeviceResync = "device.resync";
    public const string TDeviceStatus = "device.status";
    public const string TScheduleManifest = "schedule.manifest";
    public const string TScheduleAck = "schedule.ack";
    public const string TReceipt = "receipt";
    public const string TKeepAwakeSet = "keepawake.set";
    public const string TKeepAwakeClear = "keepawake.clear";
    public const string TKeepAwakeAck = "keepawake.ack";
    public const string TAnnouncementOffer = "announcement.offer";
    public const string TAnnouncementReady = "announcement.ready";
    public const string TAnnouncementReject = "announcement.reject";
    public const string TAnnouncementEnd = "announcement.end";
    public const string TMediaToken = "media.token";
    public const string TManifestRejected = "manifest.rejected";

    public static readonly string[] ReceiptStates =
        ["offered", "ready", "started", "completed", "failed", "expired", "suppressed", "unknown"];
}

public static class ReceiptState
{
    public const string Offered = "offered";
    public const string Ready = "ready";
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Suppressed = "suppressed";
    public const string Unknown = "unknown";
}

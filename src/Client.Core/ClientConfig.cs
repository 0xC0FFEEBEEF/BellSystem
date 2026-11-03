using System.Text.Json;

namespace SchoolBell.Client.Core;

/// Device configuration loaded from a JSON file (snake_case). On Windows the installer
/// writes %ProgramData%\SchoolBell\config.json after POST /api/enroll; the path is
/// injectable for tests. Secrets (device_secret, trust pin) are never logged.
public sealed class ClientConfig
{
    public string ServerUrl { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceSecret { get; set; } = "";
    public string ServerTrustPin { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string ClientVersion { get; set; } = "1.0.0-pilot";

    /// Missed-bell grace seconds (PROTOCOL §6). Default 2, configurable 0–60.
    public int MissedBellGraceSeconds { get; set; } = 2;
    public int ReconnectCapSeconds { get; set; } = 30;
    public int StatusReportSeconds { get; set; } = 60;
    public int AnnouncementVolume { get; set; } = 80;
    public int JitterBufferStartMs { get; set; } = 300;
    public int JitterBufferMaxMs { get; set; } = 2000;
    public int BellFadeMs { get; set; } = 75;

    /// Groups this device belongs to, if the installer learned them (optional).
    /// PROTOCOL v1 gives the device no group list (see report: protocol ambiguity),
    /// so group-scoped occurrences cannot be matched locally until the server tells
    /// the device its groups. Empty by default: only "all" and "devices" scopes match.
    public List<long> DeviceGroups { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ClientConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path), Json)
                  ?? throw new InvalidDataException($"config file {path} is empty");
        // Default data dir sits next to the config (ProgramData\SchoolBell\data on Windows).
        if (string.IsNullOrWhiteSpace(cfg.DataDir))
            cfg.DataDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, "data");
        cfg.Validate();
        return cfg;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl)) throw new InvalidDataException("server_url is required");
        if (string.IsNullOrWhiteSpace(DeviceId)) throw new InvalidDataException("device_id is required");
        if (string.IsNullOrWhiteSpace(DeviceSecret)) throw new InvalidDataException("device_secret is required");
        if (string.IsNullOrWhiteSpace(ServerTrustPin)) throw new InvalidDataException("server_trust_pin is required");
        if (string.IsNullOrWhiteSpace(DataDir)) throw new InvalidDataException("data_dir is required");
        var uri = new Uri(ServerUrl);
        if (uri.Scheme != "https" && uri.Scheme != "http")
            throw new InvalidDataException("server_url must be https (or http for loopback development only)");
        // http:// is allowed ONLY for loopback development (documented in PIPE.md/README; never production).
        if (uri.Scheme == "http" && !(uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]"))
            throw new InvalidDataException("http:// is only permitted for loopback development servers");
        MissedBellGraceSeconds = Math.Clamp(MissedBellGraceSeconds, 0, 60);
        ReconnectCapSeconds = Math.Max(1, ReconnectCapSeconds);
        StatusReportSeconds = Math.Max(5, StatusReportSeconds);
        AnnouncementVolume = Math.Clamp(AnnouncementVolume, 0, 100);
        JitterBufferStartMs = Math.Clamp(JitterBufferStartMs, 200, 400);
        JitterBufferMaxMs = Math.Clamp(JitterBufferMaxMs, 500, 10_000);
    }

    public string ControlWsUrl => ToWsUrl(ServerUrl, "/ws/control");
    public string MediaWsUrl(string announcementId, string token) =>
        ToWsUrl(ServerUrl, $"/ws/media?announcement_id={Uri.EscapeDataString(announcementId)}&device_id={Uri.EscapeDataString(DeviceId)}&token={Uri.EscapeDataString(token)}");

    private static string ToWsUrl(string httpUrl, string path)
    {
        var uri = new Uri(httpUrl);
        var scheme = uri.Scheme == "https" ? "wss" : "ws";
        var basePort = uri.Port > 0 && uri.Port != 80 && uri.Port != 443 ? $":{uri.Port}" : "";
        return $"{scheme}://{uri.Host}{basePort}{path}";
    }
}

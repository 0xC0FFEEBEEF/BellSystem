using Microsoft.Extensions.Configuration;

namespace SchoolBell.Server;

/// Config precedence: env (SchoolBell__Key) > config/appsettings.json > built-in defaults.
/// All values visible/configurable; there is no hidden product cap.
public sealed class ServerConfig
{
    public string Urls { get; set; } = "http://127.0.0.1:5001";
    public string DataDir { get; set; } = "";           // resolved absolute
    public string DataDirRaw { get; set; } = "./data-dev";
    public string SchoolTz { get; set; } = "";          // initial school timezone; "" until admin sets it
    public string ServerName { get; set; } = "SchoolBell";
    public int MaxConnections { get; set; } = 1000;     // visible configurable limit
    public int OutboxCap { get; set; } = 500;           // per-device outbox cap (visible)
    public int StaleAfterSeconds { get; set; } = 30;
    public string[] AllowedWsOrigins { get; set; } = []; // browser WS origin allow-list
    public string? TestSetupCode { get; set; }          // test-only deterministic bootstrap code
    public string ReleaseKeyPath { get; set; } = "/etc/schoolbell/release-signing.pub.pem";
    public string UpdateRepo { get; set; } = "0xC0FFEEBEEF/BellSystem";
    public string UpdateApiBase { get; set; } = "https://api.github.com";   // GitHub REST API (overridable for tests/mirrors)
    public string WhisperUrl { get; set; } = "http://127.0.0.1:8178";
    public string WebRoot { get; set; } = "";
    public int MinPasswordLength { get; set; } = 12;            // built office UI (wwwroot); "" = next to the binary

    public string DbPath => Path.Combine(DataDir, "schoolbell.db");
    public string SoundsDir => Path.Combine(DataDir, "sounds");
    public string KeysDir => Path.Combine(DataDir, "keys");
    public string ManifestKeyPath => Path.Combine(KeysDir, "manifest.key");

    public static ServerConfig Read(IConfiguration cfg, string contentRoot)
    {
        var s = cfg.GetSection("SchoolBell");
        var c = new ServerConfig
        {
            Urls = s["Urls"] ?? "http://127.0.0.1:5001",
            DataDirRaw = s["DataDir"] ?? "./data-dev",
            SchoolTz = s["SchoolTz"] ?? "",
            ServerName = s["ServerName"] ?? "SchoolBell",
            MaxConnections = ParseInt(s["MaxConnections"], 1000),
            OutboxCap = ParseInt(s["OutboxCap"], 500),
            StaleAfterSeconds = ParseInt(s["StaleAfterSeconds"], 30),
            TestSetupCode = s["TestSetupCode"],
            ReleaseKeyPath = s["ReleaseKeyPath"] ?? "/etc/schoolbell/release-signing.pub.pem",
            UpdateRepo = s["UpdateRepo"] ?? "0xC0FFEEBEEF/BellSystem",
            UpdateApiBase = (s["UpdateApiBase"] ?? "https://api.github.com").TrimEnd('/'),
            WhisperUrl = s["WhisperUrl"] ?? "http://127.0.0.1:8178",
            WebRoot = s["WebRoot"] ?? "",
            MinPasswordLength = ParseInt(s["MinPasswordLength"], 12),
        };
        var origins = s.GetSection("AllowedWsOrigins").Get<string[]>();
        if (origins is not null) c.AllowedWsOrigins = origins;
        c.DataDir = Path.IsPathRooted(c.DataDirRaw) ? c.DataDirRaw : Path.GetFullPath(Path.Combine(contentRoot, c.DataDirRaw));
        return c;
    }

    private static int ParseInt(string? v, int fallback) =>
        int.TryParse(v, out var x) && x > 0 ? x : fallback;
}

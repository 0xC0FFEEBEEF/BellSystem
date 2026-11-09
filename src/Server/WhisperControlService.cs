using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

public sealed record WhisperControlView(
    int CpuCores, int MinCpuCores, int MaxCpuCores, bool RequestPending,
    int? AppliedCpuCores, string State, string? Message, long? UpdatedAt);

/// Writes an unprivileged request which the root-only systemd helper validates and applies.
/// The web process never receives permission to edit units or invoke systemctl.
public sealed class WhisperControlService
{
    public const int MinCores = 1;
    public const int HardMaxCores = 16;

    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly ILogger<WhisperControlService> _log;
    private readonly string _dir;
    private string RequestPath => Path.Combine(_dir, "request.json");
    private string StatusPath => Path.Combine(_dir, "status.json");

    public WhisperControlService(Db db, ServerConfig cfg, TimeProvider time, ILogger<WhisperControlService> log)
    {
        _db = db; _time = time; _log = log;
        _dir = Path.Combine(cfg.DataDir, "whisper-control");
    }

    public int MaxCores => Math.Min(HardMaxCores, Math.Max(MinCores, Environment.ProcessorCount - 2));

    public WhisperControlView Status()
    {
        int requested = int.TryParse(_db.GetSetting("whisper_cpu_cores"), out var n)
            ? Math.Clamp(n, MinCores, MaxCores) : Math.Min(2, MaxCores);
        int? applied = null;
        string state = File.Exists(RequestPath) ? "pending" : "not_applied";
        string? message = null;
        long? at = null;
        try
        {
            if (File.Exists(StatusPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(StatusPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("cpu_cores", out var c) && c.TryGetInt32(out var cv)) applied = cv;
                if (!File.Exists(RequestPath) && root.TryGetProperty("state", out var s)) state = s.GetString() ?? state;
                if (root.TryGetProperty("message", out var m)) message = m.GetString();
                if (root.TryGetProperty("at", out var t) && t.TryGetInt64(out var tv)) at = tv;
            }
        }
        catch (Exception e)
        {
            _log.LogWarning("reading Whisper control status failed: {Error}", e.Message);
            state = "status_unavailable";
        }
        return new(requested, MinCores, MaxCores, File.Exists(RequestPath), applied, state, message, at);
    }

    public (int Status, string? Detail) Request(string actor, int? cores)
    {
        if (cores is null) return (400, "cpu_cores is required");
        if (cores < MinCores || cores > MaxCores)
            return (400, $"cpu_cores must be between {MinCores} and {MaxCores}");

        Directory.CreateDirectory(_dir);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(new { cpu_cores = cores.Value, requested_by = actor, requested_at = now }, Util.ApiJson);
        var tmp = RequestPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, RequestPath, true);
        _db.SetSetting("whisper_cpu_cores", cores.Value.ToString());
        Audit.Add(_db, actor, "whisper.resources", $"cpu_cores={cores.Value}");
        _log.LogInformation("Whisper resource change to {Cores} CPU core(s) requested by {Actor}", cores.Value, actor);
        return (202, null);
    }
}

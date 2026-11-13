using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

public sealed record WhisperControlView(
    int CpuCores, int MinCpuCores, int MaxCpuCores, bool RequestPending,
    int? AppliedCpuCores, string State, string? Message, long? UpdatedAt,
    bool? EngineAvailable = null, bool? TranscriptionEnabled = null, int? ActualThreads = null, double? CpuQuotaCores = null);

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
    private readonly IHttpClientFactory? _http;
    private readonly ServerConfig _cfg;
    private readonly LiveSettings? _settings;
    private string RequestPath => Path.Combine(_dir, "request.json");
    private string StatusPath => Path.Combine(_dir, "status.json");

    public WhisperControlService(Db db, ServerConfig cfg, TimeProvider time, ILogger<WhisperControlService> log,
        IHttpClientFactory? http = null, LiveSettings? settings = null)
    {
        _db = db; _time = time; _log = log;
        _http = http; _cfg = cfg; _settings = settings;
        _dir = Path.Combine(cfg.DataDir, "whisper-control");
    }

    public int MaxCores => Math.Min(HardMaxCores, Math.Max(MinCores, Environment.ProcessorCount - 2));

    public async Task<WhisperControlView> StatusAsync(CancellationToken ct)
    {
        var status = Status();
        if (_http is null) return status;
        bool available = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.CreateClient("whisper").GetAsync(_cfg.WhisperUrl.TrimEnd('/') + "/health", timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                available = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == "ok";
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { }
        int? threads = null;
        double? quota = null;
        // Read-only cgroup/proc inspection verifies the running service, independently of an old
        // "applied" file. No systemctl invocation or additional privileges in the web process.
        if (OperatingSystem.IsLinux())
            try
            {
                const string group = "/sys/fs/cgroup/system.slice/schoolbell-whisper.service";
                var pidText = File.ReadLines(Path.Combine(group, "cgroup.procs")).FirstOrDefault();
                if (int.TryParse(pidText, out var pid))
                {
                    var args = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0');
                    var index = Array.IndexOf(args, "--threads");
                    if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var count)) threads = count;
                }
                var cpu = File.ReadAllText(Path.Combine(group, "cpu.max")).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (cpu.Length == 2 && double.TryParse(cpu[0], out var limit) && double.TryParse(cpu[1], out var period) && period > 0) quota = limit / period;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return status with
        {
            EngineAvailable = available, TranscriptionEnabled = _settings?.TranscriptionEnabled,
            ActualThreads = threads, CpuQuotaCores = quota,
            State = status.RequestPending ? status.State : !available ? "offline" : status.State,
            Message = !available ? "Whisper is not ready or reachable; recordings remain queued." : status.Message,
        };
    }

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

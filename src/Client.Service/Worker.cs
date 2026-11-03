using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Audio;
using SchoolBell.Client.Core;

namespace SchoolBell.Client.Service;

/// Composition root: Client.Core's DeviceAgent wired to the Windows glue — pipe host, renderer
/// router (console companion or the service's own signed-out renderer), keep-awake thread, WTS
/// identity provider, client updater, EventLog. Config & data live under ProgramData\SchoolBell.
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly ILoggerFactory _logFactory;
    private readonly string _configPath;
    private readonly DeviceAgent? _agent;
    private readonly PipeServerHost? _pipe;
    private readonly RendererRouter? _router;
    private readonly KeepAwakeRunner? _keepAwake;
    private readonly ActiveUserProvider? _users;
    private readonly ClientUpdater? _updater;
    private readonly ClientConfig? _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private string? _lastUserSid;
    private string? _updateState;

    public Worker(IConfiguration config, ILoggerFactory logFactory)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<Worker>();
        _configPath = config["SchoolBell:ConfigFile"] is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SchoolBell", "config.json");

        if (!File.Exists(_configPath))
        {
            _log.LogError("SchoolBell is not enrolled yet: {ConfigPath} is missing. " +
                          "Run SchoolBellSetup.exe with --server and --enrollment-file.", _configPath);
            return;
        }

        try
        {
            _log.LogInformation("startup: loading config");
            var cfg = ClientConfig.Load(_configPath);
            cfg.ClientVersion = ClientUpdater.AppVersion();
            _cfg = cfg;
            _users = new ActiveUserProvider(logFactory.CreateLogger<ActiveUserProvider>());
            _pipe = new PipeServerHost(cfg, logFactory.CreateLogger<PipeServerHost>());
            _log.LogInformation("startup: creating the service renderer");
            var headless = new AudioEngine(logFactory.CreateLogger("SchoolBell.ServiceRenderer"), "service")
            {
                JitterStartMs = cfg.JitterBufferStartMs, JitterMaxMs = cfg.JitterBufferMaxMs,
            };
            _router = new RendererRouter(_pipe, headless, new VolumeRecoveryStore(cfg.DataDir),
                logFactory.CreateLogger<RendererRouter>());
            _log.LogInformation("startup: service renderer ready");
            _keepAwake = new KeepAwakeRunner();
            _updater = new ClientUpdater(cfg, _http, logFactory.CreateLogger<ClientUpdater>());
            _agent = new DeviceAgent(cfg, TimeProvider.System, _http,
                logFactory.CreateLogger<SchoolBellDeviceAgent>(),
                _router, _router, _router, _users, _keepAwake, _updater);
            _updater.SetQuietCheck(() => _agent.IsQuietPeriod());
            _pipe.Attach(_agent);
            _pipe.ServiceState = () => new PipeServiceState(_agent.Connected, cfg.ServerUrl, _router.Owner,
                Environment.MachineName, cfg.ClientVersion, _updateState);
            if (_agent.Settings.Current is { } s)
                headless.ApplySettings(new AudioSettings(s.OutputDevice, _agent.AnnouncementVolume, s.VolumeCap));
            _agent.SettingsChanged += s =>
            {
                headless.ApplySettings(new AudioSettings(s.OutputDevice, _agent.AnnouncementVolume, s.VolumeCap));
                _pipe.BroadcastSettings();
            };
            headless.StatusChanged += () => { if (_pipe.Renderer is null) _agent.NotifyStateChanged(); };
        }
        catch (Exception e)
        {
            _log.LogCritical("failed to start device agent at {ConfigPath}: {Error}", _configPath, SafeMessage(e.Message));
            _agent = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_agent is null || _pipe is null || _keepAwake is null || _router is null || _cfg is null) return;

        _keepAwake.Configure(_agent.KeepAwakePolicies, _agent.Clock, _logFactory.CreateLogger<KeepAwakeRunner>());
        _keepAwake.Start();
        _router.RecoverPendingVolume();

        _agent.ManifestAccepted += _ => _pipe.SendManifestState();
        _agent.StateChanged += () => _pipe.SendKeepAwakeState();
        _agent.HistoryChanged += () => _pipe.BroadcastHistory();
        _agent.ConnectionChanged += connected =>
        {
            _pipe.BroadcastState();
            if (connected) WriteHealth();
        };

        _log.LogInformation("SchoolBell service {Version} starting (computer {Computer})", _cfg.ClientVersion, Environment.MachineName);

        await Task.WhenAll(
            _agent.RunAsync(stoppingToken),
            _pipe.RunAcceptLoopAsync(stoppingToken),
            WatchIdentityAsync(stoppingToken));
    }

    /// Shared computers: a change of console user clears targeted history and refreshes status.
    private async Task WatchIdentityAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (_, sid) = _users!.GetActiveUser();
            if (sid != _lastUserSid)
            {
                if (_lastUserSid is not null) _agent!.OnActiveUserChanged();
                _lastUserSid = sid;
                _agent!.NotifyStateChanged();
                _pipe!.BroadcastState();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// Health marker for update-runner.ps1: written after each successful server authentication.
    private void WriteHealth()
    {
        try
        {
            var path = Path.Combine(_cfg!.DataDir, "health.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                version = _cfg.ClientVersion,
                connected_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                pid = Environment.ProcessId,
            }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { _log.LogWarning("health marker write failed: {Error}", e.Message); }
    }

    private static string SafeMessage(string message) =>
        message.Contains("device_secret") ? "[message suppressed: possible secret echo]" : message;
}

internal sealed class SchoolBellDeviceAgent;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;

namespace SchoolBell.Client.Service;

/// Composition root: Client.Core's DeviceAgent wired to the Windows glue
/// (pipe host, keep-awake thread, WTS identity provider, EventLog).
/// Config & data live under ProgramData\SchoolBell (paths injectable for tests).
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly ILoggerFactory _logFactory;
    private readonly string _configPath;
    private readonly DeviceAgent? _agent;
    private readonly PipeServerHost? _pipe;
    private readonly KeepAwakeRunner? _keepAwake;
    private readonly ActiveUserProvider? _users;
    private readonly HttpClient _http = new();

    public Worker(IConfiguration config, ILoggerFactory logFactory)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<Worker>();
        _configPath = config["SchoolBell:ConfigFile"]
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                          "SchoolBell", "config.json");

        if (!File.Exists(_configPath))
        {
            // Enrollment (POST /api/enroll) is the installer's job; until it has run there
            // is no device identity and the service idles honestly instead of guessing.
            _log.LogError("SchoolBell is not enrolled yet: {ConfigPath} is missing. " +
                          "Run SchoolBellSetup.exe with --server and --enrollment-file.", _configPath);
            return;
        }

        try
        {
            var cfg = ClientConfig.Load(_configPath);
            _users = new ActiveUserProvider(logFactory.CreateLogger<ActiveUserProvider>());
            _pipe = new PipeServerHost(cfg, _users, logFactory.CreateLogger<PipeServerHost>());
            _keepAwake = new KeepAwakeRunner();
            _agent = new DeviceAgent(cfg, TimeProvider.System, _http,
                logFactory.CreateLogger<SchoolBellDeviceAgent>(),
                _pipe, _pipe, _pipe, _users, _keepAwake);
            _pipe.Attach(_agent);
        }
        catch (Exception e)
        {
            _log.LogCritical("failed to start device agent at {ConfigPath}: {Error}",
                _configPath, SafeMessage(e.Message));
            _agent = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_agent is null || _pipe is null || _keepAwake is null) return;

        _keepAwake.Configure(_agent.KeepAwakePolicies, _agent.Clock,
            _logFactory.CreateLogger<KeepAwakeRunner>());
        _keepAwake.Start();

        _agent.ManifestAccepted += _ => _pipe.SendManifestState();
        _agent.StateChanged += () => _pipe.SendKeepAwakeState();

        _log.LogInformation("SchoolBell service starting (computer {Computer})", Environment.MachineName);

        await Task.WhenAll(
            _agent.RunAsync(stoppingToken),
            _pipe.RunAcceptLoopAsync(stoppingToken));
    }

    /// Defensive: drop any exception message that could echo config content into the EventLog.
    private static string SafeMessage(string message) =>
        message.Contains("device_secret") ? "[message suppressed: possible secret echo]" : message;
}

/// Named logger category for the agent (keeps EventLog lines under "SchoolBell.DeviceAgent").
internal sealed class SchoolBellDeviceAgent;

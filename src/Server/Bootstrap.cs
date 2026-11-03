using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

/// Startup sequence: migrations, trust pin, manifest key, settings seeds, built-in sounds,
/// one-time admin setup code (printed exactly once to stdout).
public sealed class Bootstrap
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly SoundService _sounds;
    private readonly AuthService _auth;
    private readonly ILogger<Bootstrap> _log;

    public Bootstrap(Db db, ServerConfig cfg, SoundService sounds, AuthService auth, ILogger<Bootstrap> log)
    {
        _db = db; _cfg = cfg; _sounds = sounds; _auth = auth; _log = log;
    }
    public void Run(string? contentRoot)
    {
        _db.Init();
        var trustPin = TrustPin.Get(_db);                       // first-start creation, stored in settings
        SigningKey.Load(_cfg, trustPin);                        // DataDir/keys/manifest.key, 0600
        if (string.IsNullOrEmpty(_db.GetSetting("server_name")))
            _db.SetSetting("server_name", _cfg.ServerName);
        if (string.IsNullOrEmpty(_db.GetSetting("school_tz")) && !string.IsNullOrEmpty(_cfg.SchoolTz))
        {
            if (NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(_cfg.SchoolTz) is null)
                _log.LogWarning("configured SchoolTz '{Tz}' is not a valid IANA id; ignoring", _cfg.SchoolTz);
            else
                _db.SetSetting("school_tz", _cfg.SchoolTz);
        }
        var soundsSrc = Util.FindUpwards(contentRoot, "legacy", "sounds")
            ?? Util.FindUpwards(AppContext.BaseDirectory, "legacy", "sounds")
            ?? Util.FindUpwards(Directory.GetCurrentDirectory(), "legacy", "sounds");
        _sounds.SeedBuiltIn(soundsSrc);
        _auth.EnsureBootstrap();                                // prints SETUP CODE once if no users
    }
}

/// Runs the bootstrap as a hosted service so it also executes under test hosts
/// (WebApplicationFactory never resumes Program past Build()).
public sealed class BootstrapHost : IHostedService
{
    private readonly Bootstrap _bootstrap;
    private readonly IHostEnvironment _env;

    public BootstrapHost(Bootstrap bootstrap, IHostEnvironment env)
    {
        _bootstrap = bootstrap; _env = env;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _bootstrap.Run(_env.ContentRootPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using SchoolBell.Server;

namespace Server.Tests;

public sealed record LogLine(string Category, LogLevel Level, string Message, string? Exception);

/// Captures every log entry (formatted message + structured state values) so tests can prove
/// secrets never reach the log pipeline.
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<LogLine> Lines { get; } = [];
    public string AllText => string.Join("\n", Lines.Select(l => $"{l.Category} [{l.Level}] {l.Message} {l.Exception}"));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> kv)
                foreach (var pair in kv)
                    if (pair.Key != "{OriginalFormat}")
                        text += " | " + pair.Key + "=" + pair.Value;
            lock (owner.Lines) owner.Lines.Add(new LogLine(category, logLevel, text, exception?.ToString()));
        }
    }
}

public sealed class MutableTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// Service-layer test environment: real services, real SQLite in a temp dir, fake clock.
public sealed class TestEnv : IDisposable
{
    public string Dir { get; }
    public ServerConfig Cfg { get; }
    public MutableTime Time { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public ILoggerFactory LogFactory { get; }

    public Db Database { get; }
    public AuthService Auth { get; }
    public EnrollmentService Enrollment { get; }
    public ControlRegistry Registry { get; }
    public OutboxService Outbox { get; }
    public DevicesService Devices { get; }
    public ReceiptsService Receipts { get; }
    public SoundService Sounds { get; }
    public TemplatesService Templates { get; }
    public PublishService Publish { get; }
    public KeepAwakeService KeepAwake { get; }
    public DiagnosticsService Diagnostics { get; }
    public ControlHub Hub { get; }

    public TestEnv(string tz = "", int outboxCap = 500, DateTimeOffset? now = null)
    {
        Dir = Path.Combine(Path.GetTempPath(), "schoolbell-s1-test-" + Guid.NewGuid().ToString("n"));
        Cfg = new ServerConfig
        {
            DataDirRaw = Dir,
            DataDir = Dir,
            OutboxCap = outboxCap,
            SchoolTz = tz,
        };
        if (now is not null) Time.Now = now.Value;
        else Time.Now = DateTimeOffset.UtcNow;

        LogFactory = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));

        Database = new Db(Cfg);
        Database.Init();
        var pin = TrustPin.Get(Database);
        SigningKey.Load(Cfg, pin);

        Registry = new ControlRegistry(Cfg, LogFactory.CreateLogger<ControlRegistry>());
        Auth = new AuthService(Database, Cfg, Time, LogFactory.CreateLogger<AuthService>());
        Enrollment = new EnrollmentService(Database, Time, LogFactory.CreateLogger<EnrollmentService>());
        Sounds = new SoundService(Database, Cfg, LogFactory.CreateLogger<SoundService>());
        Sounds.SeedBuiltIn(Util.FindUpwards(AppContext.BaseDirectory, "legacy", "sounds"));
        Outbox = new OutboxService(Database, Cfg, Time, Registry, LogFactory.CreateLogger<OutboxService>());
        Devices = new DevicesService(Database, Cfg, Time, Registry);
        Receipts = new ReceiptsService(Database, Time, LogFactory.CreateLogger<ReceiptsService>());
        Templates = new TemplatesService(Database, Time, Sounds);
        Publish = new PublishService(Database, Cfg, Time, Templates, Devices, Sounds, Outbox,
            LogFactory.CreateLogger<PublishService>());
        KeepAwake = new KeepAwakeService(Database, Time, Outbox, Devices);
        Hub = new ControlHub(Database, Cfg, Time, Registry, Outbox, Devices, Receipts, KeepAwake, Publish,
            LogFactory.CreateLogger<ControlHub>());
        Diagnostics = new DiagnosticsService(Database, Cfg, Time, Registry, Outbox, Hub);
    }

    public void SetTz(string tz) => Database.SetSetting("school_tz", tz);

    public CurrentUser Admin(int id = 1) =>
        new(id, "admin", "admin", [], true, "test-session");

    public CurrentUser User(string username, string role, List<long>? groups = null, bool schoolwide = false) =>
        new(999, username, role, groups ?? [], schoolwide, "test-session");

    public void EnsureTzConfigured() => SetTz("America/Chicago");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

/// HTTP test app: WebApplicationFactory<Program> with an isolated DataDir and test bootstrap code.
public sealed class TestApp : IDisposable
{
    public const string SetupCode = "test-setup-code-24hex";
    public string Dir { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public CapturingLoggerProvider Logs { get; } = new();
    private readonly HttpClient _client;
    private string? _session;
    private string? _csrf;

    public TestApp(string tz = "", int outboxCap = 500)
    {
        Dir = Path.Combine(Path.GetTempPath(), "schoolbell-s1-http-" + Guid.NewGuid().ToString("n"));
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // NOTE: UseSetting lands BEFORE the app's own AddJsonFile in minimal hosting, so
            // appsettings.json would win over it. ConfigureAppConfiguration callbacks run last
            // and win.
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SchoolBell:DataDir"] = Dir,
                ["SchoolBell:TestSetupCode"] = SetupCode,
                ["SchoolBell:OutboxCap"] = outboxCap.ToString(),
                ["SchoolBell:SchoolTz"] = tz,
            }));
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        });
        _client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public Task<HttpResponseMessage> Get(string path) => Send("GET", path, null);
    public Task<HttpResponseMessage> Post(string path, object? body) => Send("POST", path, body);
    public Task<HttpResponseMessage> Put(string path, object? body) => Send("PUT", path, body);
    public Task<HttpResponseMessage> Delete(string path) => Send("DELETE", path, null);

    /// Raw call with no cookies and no X-CSRF header (client-behavior tests).
    public async Task<HttpResponseMessage> SendBare(string method, string path, object? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, UtilApiJson), Encoding.UTF8, "application/json");
        return await _client.SendAsync(req);
    }

    public async Task LoginAs(string username, string password)
    {
        await FetchCsrf();
        var login = await Post("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private async Task<HttpResponseMessage> Send(string method, string path, object? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        var cookies = new List<string>();
        if (_csrf is not null) cookies.Add($"sb_csrf={_csrf}");
        if (_session is not null) cookies.Add($"sb_session={_session}");
        if (cookies.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (_csrf is not null) req.Headers.TryAddWithoutValidation("X-CSRF", _csrf);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, UtilApiJson), Encoding.UTF8, "application/json");
        var res = await _client.SendAsync(req);
        CaptureCookies(res);
        return res;
    }

    private static readonly JsonSerializerOptions UtilApiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private void CaptureCookies(HttpResponseMessage res)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var raw in values)
        {
            var pair = raw.Split(';')[0];
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var name = pair[..idx];
            var value = pair[(idx + 1)..];
            if (name == "sb_csrf") _csrf = value;
            if (name == "sb_session") _session = value.Length > 0 ? value : null;
        }
    }

    public async Task FetchCsrf()
    {
        var res = await Get("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    public async Task LoginAsAdmin()
    {
        await FetchCsrf();
        var res = await Post("/api/auth/setup", new { code = SetupCode, username = "admin", password = "correct-horse99!" });
        // Idempotent: a re-call finds setup already consumed (403) and just logs in.
        Assert.True(res.StatusCode is HttpStatusCode.Created or HttpStatusCode.Forbidden,
            $"setup returned {(int)res.StatusCode}");
        await FetchCsrf();
        var login = await Post("/api/auth/login", new { username = "admin", password = "correct-horse99!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    public async Task<CurrentUser> CreateUser(string username, string password, string role,
        List<long>? groups = null, bool schoolwide = false)
    {
        var res = await Post("/api/admin/users", new
        {
            username, password, role,
            group_scope = groups ?? new List<long>(),
            schoolwide_broadcast = schoolwide,
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return new CurrentUser(0, username, role, groups ?? [], schoolwide, "http-session");
    }

    public CurrentUser AdminUser() => new(1, "admin", "admin", [], true, "http-session");

    /// Multipart upload with the session cookie and CSRF header.
    public Task<HttpResponseMessage> Upload(string path, string field, string fileName, byte[] bytes,
        (string Name, string Value)? extra = null, string method = "POST")
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), field, fileName);
        if (extra is { } e) form.Add(new StringContent(e.Value), e.Name);
        return SendMultipart(path, form, method);
    }

    public async Task<HttpResponseMessage> SendMultipart(string path, MultipartFormDataContent form, string method = "POST")
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path) { Content = form };
        var cookies = new List<string>();
        if (_csrf is not null) cookies.Add($"sb_csrf={_csrf}");
        if (_session is not null) cookies.Add($"sb_session={_session}");
        if (cookies.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (_csrf is not null) req.Headers.TryAddWithoutValidation("X-CSRF", _csrf);
        var res = await _client.SendAsync(req);
        CaptureCookies(res);
        return res;
    }

    /// A second, independent office session against the same server (e.g. an operator).
    public sealed class Session : IDisposable
    {
        private readonly HttpClient _c;
        private string? _csrf, _session;
        public Session(TestApp app) => _c = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        public async Task LoginAs(string username, string password)
        {
            await Send("GET", "/api/auth/csrf", null);
            var res = await Send("POST", "/api/auth/login", new { username, password });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        public Task<HttpResponseMessage> Post(string path, object? body) => Send("POST", path, body);

        public Task<HttpResponseMessage> Upload(string path, string field, string fileName, byte[] bytes)
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), field, fileName);
            return Send("POST", path, null, form);
        }

        private async Task<HttpResponseMessage> Send(string method, string path, object? body, HttpContent? content = null)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), path);
            var cookies = new List<string>();
            if (_csrf is not null) cookies.Add($"sb_csrf={_csrf}");
            if (_session is not null) cookies.Add($"sb_session={_session}");
            if (cookies.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
            if (_csrf is not null) req.Headers.TryAddWithoutValidation("X-CSRF", _csrf);
            if (content is not null) req.Content = content;
            else if (body is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(body, UtilApiJson), Encoding.UTF8, "application/json");
            var res = await _c.SendAsync(req);
            if (res.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var raw in values)
                {
                    var pair = raw.Split(';')[0]; var idx = pair.IndexOf('=');
                    if (idx <= 0) continue;
                    if (pair[..idx] == "sb_csrf") _csrf = pair[(idx + 1)..];
                    if (pair[..idx] == "sb_session") _session = pair[(idx + 1)..] is { Length: > 0 } v ? v : null;
                }
            return res;
        }

        public void Dispose() => _c.Dispose();
    }

    public Task<JsonElement> Json(HttpResponseMessage res) => res.Content.ReadAsJsonElement();

    public void Dispose()
    {
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

internal static class HttpJsonExt
{
    public static async Task<JsonElement> ReadAsJsonElement(this HttpContent content)
    {
        var text = await content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement;
    }
}

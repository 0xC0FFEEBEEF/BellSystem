using System.Text.Json;
using Microsoft.Extensions.Configuration.Json;
using SchoolBell.Contracts;
using SchoolBell.Server;

var builder = WebApplication.CreateBuilder(args);

// JSON config file (config/appsettings.json) + env overrides (SchoolBell__Urls etc.).
// Inserted FIRST so the standard sources (env vars, command line) override it;
// ServerConfig is resolved from the FINAL host configuration on first use so
// config sources added by tests/hosts are honored.
builder.Configuration.Sources.Insert(0, new JsonConfigurationSource
{
    Path = Path.Combine("config", "appsettings.json"),
    Optional = true,
    ReloadOnChange = false,
});
builder.WebHost.UseUrls(builder.Configuration["SchoolBell:Urls"] ?? "http://127.0.0.1:5001");

// Admin > Server log: every line also goes to an in-memory/daily-file log the office can read and
// download. The provider gets everything; LogBuffer.Accepts decides (Verbose adds debug detail).
var logBuffer = new LogBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new LogBufferProvider(logBuffer));
builder.Logging.AddFilter<LogBufferProvider>(null, LogLevel.Trace);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => ServerConfig.Read(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<DirectoryService>();
builder.Services.AddSingleton<ControlRegistry>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<SoundService>();
builder.Services.AddSingleton<OutboxService>();
builder.Services.AddSingleton<DevicesService>();
builder.Services.AddSingleton<ReceiptsService>();
builder.Services.AddSingleton<ClientLogsService>();
builder.Services.AddSingleton<TemplatesService>();
builder.Services.AddSingleton<PublishService>();
builder.Services.AddSingleton<KeepAwakeService>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddSingleton<ControlHub>();
builder.Services.AddSingleton<Bootstrap>();
builder.Services.AddHostedService<BootstrapHost>();
builder.Services.AddHostedService<ScheduleRenewalHost>();

// ---- milestone S2: talk lease, live announcements, media relay, recordings, history ----
builder.Services.AddSingleton<LiveSettings>();
builder.Services.AddSingleton<LiveMetrics>();
builder.Services.AddSingleton<MediaRouter>();
builder.Services.AddSingleton<TalkLeaseService>();
builder.Services.AddSingleton<AnnouncementsService>();
builder.Services.AddSingleton<LiveWsHub>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("github", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("whisper", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<ITranscriptionEngine, WhisperCppEngine>();   // loopback whisper.cpp server
builder.Services.AddSingleton<WhisperControlService>();
builder.Services.AddSingleton<LiveTranscriptionService>();
builder.Services.AddSingleton<DeviceSettingsService>();
builder.Services.AddSingleton<ReleaseTrust>();
builder.Services.AddSingleton<ClientUpdateService>();
builder.Services.AddSingleton<ServerUpdateService>();
builder.Services.AddHostedService<UpdatesHost>();
builder.Services.AddHostedService<AnnouncementSweeperHost>();
builder.Services.AddHostedService<TranscriptionWorkerHost>();
builder.Services.AddHostedService<RetentionWorkerHost>();

var app = builder.Build();
logBuffer.EnableFiles(Path.Combine(app.Services.GetRequiredService<ServerConfig>().DataDir, "logs"));
logBuffer.SchoolTimeZone = () => app.Services.GetRequiredService<PublishService>().SchoolTz;

// MediaRouter callbacks are assigned (not constructor-injected) so the media path never depends on
// the announcement service and the dependency graph stays acyclic.
var mediaRouter = app.Services.GetRequiredService<MediaRouter>();
var announcementsService = app.Services.GetRequiredService<AnnouncementsService>();
mediaRouter.OnFirstFrameSent = announcementsService.RecordStreamStarted;
mediaRouter.OnQueueOverflow = announcementsService.RecordQueueOverflow;
mediaRouter.OnStreamClosed = announcementsService.RecordMediaSocketClosed;

// Office web UI (React build output). Served from WebRoot or wwwroot next to the binary;
// unknown non-API paths fall back to index.html so /admin and other client routes deep-link.
var cfgForUi = app.Services.GetRequiredService<ServerConfig>();
var webRoot = cfgForUi.WebRoot is { Length: > 0 } wr ? wr : Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(webRoot) && Util.FindUpwards(app.Environment.ContentRootPath, "web", "dist") is { } devDist) webRoot = devDist;
var uiFiles = Directory.Exists(webRoot) ? new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot) : null;
app.Use(async (ctx, next) =>
{
    // conservative security headers for the office UI
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=()";
    await next();
});
if (uiFiles is not null)
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = uiFiles });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = uiFiles,
        OnPrepareResponse = c =>
        {
            if (c.File.Name == "index.html") c.Context.Response.Headers.CacheControl = "no-cache";
            else if (c.Context.Request.Path.StartsWithSegments("/assets")) c.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        },
    });
}

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(Protocol.HeartbeatSeconds), // ping every 10 s
    // A device that stops answering pings for 30 s is disconnected, so "connected" in the
    // registry always means contact within the stale threshold (PROTOCOL.md §3).
    KeepAliveTimeout = TimeSpan.FromSeconds(Protocol.StaleAfterSeconds),
});
app.UseMiddleware<SessionMiddleware>();
app.UseMiddleware<CsrfMiddleware>();

AuthEndpoints.Map(app);
EnrollmentEndpoints.Map(app);
ScheduleEndpoints.Map(app);
AdminEndpoints.Map(app);
DeviceEndpoints.Map(app);
AnnouncementsEndpoints.Map(app);
ExtraEndpoints.Map(app);
LogEndpoints.Map(app);
ClientLogEndpoints.Map(app);

app.Map("/ws/control", async (HttpContext ctx, ControlHub hub) => await hub.HandleAsync(ctx));
// Office microphone (cookie session + Origin check + talk lease) and device media (single-use OTP).
app.Map("/ws/mic", async (HttpContext ctx, LiveWsHub ws) => await ws.HandleMicAsync(ctx));
app.Map("/ws/media", async (HttpContext ctx, LiveWsHub ws) => await ws.HandleMediaAsync(ctx));

if (uiFiles is not null)
{
    app.MapFallback(async ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/ws"))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { detail = "no such endpoint" }, Util.ApiJson);
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.SendFileAsync(uiFiles.GetFileInfo("index.html"));
    });
}
else
{
    app.MapGet("/", () => Results.Json(new
    {
        server = "schoolbell",
        version = Util.ServerVersion,
        status = "API running; the office UI build (web/dist or wwwroot) was not found",
    }, Util.ApiJson));
}

app.Run();

public partial class Program { }

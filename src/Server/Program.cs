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
builder.Services.AddSingleton<TemplatesService>();
builder.Services.AddSingleton<PublishService>();
builder.Services.AddSingleton<KeepAwakeService>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddSingleton<ControlHub>();
builder.Services.AddSingleton<Bootstrap>();
builder.Services.AddHostedService<BootstrapHost>();

// ---- milestone S2: talk lease, live announcements, media relay, recordings, history ----
builder.Services.AddSingleton<LiveSettings>();
builder.Services.AddSingleton<LiveMetrics>();
builder.Services.AddSingleton<MediaRouter>();
builder.Services.AddSingleton<TalkLeaseService>();
builder.Services.AddSingleton<AnnouncementsService>();
builder.Services.AddSingleton<LiveWsHub>();
builder.Services.AddSingleton<ITranscriptionEngine, NoTranscriptionEngine>();   // S3 wires whisper.cpp
builder.Services.AddHostedService<AnnouncementSweeperHost>();
builder.Services.AddHostedService<TranscriptionWorkerHost>();
builder.Services.AddHostedService<RetentionWorkerHost>();

var app = builder.Build();

// MediaRouter callbacks are assigned (not constructor-injected) so the media path never depends on
// the announcement service and the dependency graph stays acyclic.
var mediaRouter = app.Services.GetRequiredService<MediaRouter>();
var announcementsService = app.Services.GetRequiredService<AnnouncementsService>();
mediaRouter.OnFirstFrameSent = announcementsService.RecordStreamStarted;
mediaRouter.OnQueueOverflow = announcementsService.RecordQueueOverflow;
mediaRouter.OnStreamClosed = announcementsService.RecordMediaSocketClosed;

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(Protocol.HeartbeatSeconds), // ping every 10 s
});
app.UseMiddleware<SessionMiddleware>();
app.UseMiddleware<CsrfMiddleware>();

AuthEndpoints.Map(app);
EnrollmentEndpoints.Map(app);
ScheduleEndpoints.Map(app);
AdminEndpoints.Map(app);
DeviceEndpoints.Map(app);
AnnouncementsEndpoints.Map(app);

app.Map("/ws/control", async (HttpContext ctx, ControlHub hub) => await hub.HandleAsync(ctx));
// Office microphone (cookie session + Origin check + talk lease) and device media (single-use OTP).
app.Map("/ws/mic", async (HttpContext ctx, LiveWsHub ws) => await ws.HandleMicAsync(ctx));
app.Map("/ws/media", async (HttpContext ctx, LiveWsHub ws) => await ws.HandleMediaAsync(ctx));

app.MapGet("/", () => Results.Json(new
{
    server = "schoolbell",
    version = Util.ServerVersion,
    status = "api only (office UI arrives with the web milestone); milestone S2 live: talk lease, live announcements, media relay, recordings, history",
}, Util.ApiJson));

app.Run();

public partial class Program { }

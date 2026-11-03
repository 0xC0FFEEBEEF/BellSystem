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

var app = builder.Build();

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

app.Map("/ws/control", async (HttpContext ctx, ControlHub hub) => await hub.HandleAsync(ctx));

app.MapGet("/", () => Results.Json(new
{
    server = "schoolbell",
    version = Util.ServerVersion,
    status = "api only in milestone S1 (office UI arrives with the web milestone)",
}, Util.ApiJson));

app.Run();

public partial class Program { }

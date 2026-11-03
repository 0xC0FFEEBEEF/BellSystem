using SchoolBell.Client.Service;

// Windows service host for SchoolBell (installed as "SchoolBell" via SCM; runs as a
// console app during development). All Windows-specific behavior is injected into the
// cross-platform DeviceAgent from Client.Core through the service components below.
// --warmup: load the runtime and SchoolBell's assemblies once and exit. The installer and the
// update runner call this before starting the service, because the first launch after new files
// are written can exceed the Service Control Manager's 30 s start timeout while antivirus scans them.
if (args.Contains("--warmup"))
{
    _ = typeof(SchoolBell.Client.Audio.AudioEngine).Assembly;
    _ = typeof(SchoolBell.Client.Core.DeviceAgent).Assembly;
    _ = typeof(SchoolBell.Contracts.Protocol).Assembly;
    _ = typeof(Microsoft.Extensions.Hosting.Host).Assembly;
    _ = typeof(System.Net.WebSockets.ClientWebSocket).Assembly;
    _ = typeof(System.Text.Json.JsonSerializer).Assembly;
    Console.WriteLine("warmup ok " + SchoolBell.Client.Service.ClientUpdater.AppVersion());
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SchoolBell");

// Operational events go to the Windows EventLog under source "SchoolBell" (never secrets).
builder.Logging.AddProvider(new SchoolBellEventLogProvider());
builder.Logging.AddProvider(new ServiceFileLogProvider());
// EventLog gets SchoolBell's own operational events only (not framework chatter)
builder.Logging.AddFilter<SchoolBellEventLogProvider>("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter<SchoolBellEventLogProvider>("System", LogLevel.Warning);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;

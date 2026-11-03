using SchoolBell.Client.Service;

// Windows service host for SchoolBell (installed as "SchoolBell" via SCM; runs as a
// console app during development). All Windows-specific behavior is injected into the
// cross-platform DeviceAgent from Client.Core through the service components below.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SchoolBell");

// Operational events go to the Windows EventLog under source "SchoolBell" (never secrets).
builder.Logging.AddProvider(new SchoolBellEventLogProvider());

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SchoolBell.Server;

namespace Server.Tests;

public class WhisperControlTests
{
    [Fact]
    public void Request_is_bounded_audited_and_written_atomically()
    {
        using var env = new TestEnv();
        var service = new WhisperControlService(env.Database, env.Cfg, env.Time,
            env.LogFactory.CreateLogger<WhisperControlService>());
        Assert.Equal(400, service.Request("admin", 0).Status);
        Assert.Equal(400, service.Request("admin", service.MaxCores + 1).Status);

        int cores = Math.Min(8, service.MaxCores);
        Assert.Equal(202, service.Request("admin", cores).Status);
        var status = service.Status();
        Assert.Equal(cores, status.CpuCores);
        Assert.True(status.RequestPending);
        using var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(env.Dir, "whisper-control", "request.json")));
        Assert.Equal(cores, request.RootElement.GetProperty("cpu_cores").GetInt32());
        Assert.Equal("admin", request.RootElement.GetProperty("requested_by").GetString());
        Assert.False(File.Exists(Path.Combine(env.Dir, "whisper-control", "request.json.tmp")));
    }

    [Fact]
    public async Task Endpoint_requires_admin_and_rejects_out_of_range_values()
    {
        using var app = new TestApp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Get("/api/admin/whisper")).StatusCode);
        await app.LoginAsAdmin();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Put("/api/admin/whisper", new { cpu_cores = 0 })).StatusCode);
        var response = await app.Put("/api/admin/whisper", new { cpu_cores = 1 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await app.Json(response);
        Assert.Equal(1, json.GetProperty("cpu_cores").GetInt32());
        Assert.True(json.GetProperty("request_pending").GetBoolean());
    }
}

using System.Net;
using SchoolBell.Server;

namespace Server.Tests;

public class BellNotificationTests
{
    [Fact]
    public async Task Admin_can_disable_and_change_duration_with_range_validation()
    {
        using var app = new TestApp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Get("/api/admin/device-defaults")).StatusCode);
        await app.LoginAsAdmin();
        Assert.Equal(12, (await app.Json(await app.Get("/api/admin/device-defaults"))).GetProperty("bell_notification_seconds").GetInt32());
        foreach (var seconds in new[] { 0, 45, 300 })
        {
            var res = await app.Put("/api/admin/device-defaults", new { bell_notification_seconds = seconds });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(seconds, (await app.Json(res)).GetProperty("bell_notification_seconds").GetInt32());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Put("/api/admin/device-defaults", new { bell_notification_seconds = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Put("/api/admin/device-defaults", new { bell_notification_seconds = 301 })).StatusCode);
        Assert.Equal(300, (await app.Json(await app.Get("/api/admin/device-defaults"))).GetProperty("bell_notification_seconds").GetInt32());
    }
}

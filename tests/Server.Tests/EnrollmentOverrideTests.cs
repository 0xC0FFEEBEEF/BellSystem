using System.Net;
using SchoolBell.Server;

namespace Server.Tests;

/// Admin > Enrollment tokens: an enrollment that ran out or expired can be reopened, so installers
/// already deployed with it work again; refusals say why in the server log.
public class EnrollmentOverrideTests
{
    static async Task<(long Id, string Token)> CreateAsync(TestApp app, string label, int maxUses)
    {
        var t = await app.Json(await app.Post("/api/admin/enrollment-tokens", new { label, ttl_hours = 24, max_uses = maxUses }));
        return (t.Long("id"), t.Str("token"));
    }

    static Task<HttpResponseMessage> EnrollAsync(TestApp app, string token, string name) =>
        app.SendBare("POST", "/api/enroll", new { token, device_name = name });

    static async Task<List<string>> LogAsync(TestApp app, string q) =>
        (await app.Json(await app.Get($"/api/admin/logs?lines=200&q={Uri.EscapeDataString(q)}"))).GetProperty("lines")
            .EnumerateArray().Select(l => l.GetString()!).ToList();

    [Fact]
    public async Task A_used_up_enrollment_works_again_after_allowing_more_computers()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        var (id, token) = await CreateAsync(app, "RJHLab", 1);
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(app, token, "RHS-1")).StatusCode);

        var refused = await EnrollAsync(app, token, "RHS-2");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(await LogAsync(app, "refused"), l => l.Contains("RHS-2") && l.Contains("already used") && l.Contains("RJHLab"));

        var extended = await app.Json(await app.Post($"/api/admin/enrollment-tokens/{id}/extend", new { add_uses = 5 }));
        Assert.Equal(6, extended.Int("max_uses"));
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(app, token, "RHS-2")).StatusCode);
    }

    [Fact]
    public async Task An_expired_enrollment_works_again_after_extending_and_a_revoked_one_stays_closed()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        var (id, token) = await CreateAsync(app, "Lab2", 10);
        using (var c = LiveTest.DbOf(app).Open()) c.Run("UPDATE enrollment_tokens SET expires_at = 1 WHERE id = $id", ("$id", id));
        Assert.Equal(HttpStatusCode.Forbidden, (await EnrollAsync(app, token, "RHS-3")).StatusCode);
        Assert.Contains(await LogAsync(app, "refused"), l => l.Contains("RHS-3") && l.Contains("expired"));

        var extended = await app.Json(await app.Post($"/api/admin/enrollment-tokens/{id}/extend", new { extend_hours = 24 }));
        Assert.True(extended.Long("expires_at") > DateTimeOffset.UtcNow.AddHours(23).ToUnixTimeMilliseconds());
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(app, token, "RHS-3")).StatusCode);

        Assert.True((await app.Delete($"/api/admin/enrollment-tokens/{id}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Post($"/api/admin/enrollment-tokens/{id}/extend", new { add_uses = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnrollAsync(app, token, "RHS-4")).StatusCode);
    }
}

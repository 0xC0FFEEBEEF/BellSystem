using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

public class TranscriptNotificationTests
{
    static async Task<string> Recording(TestApp app, string target = "{\"all\":true}")
    {
        var result = await app.Upload("/api/announcements/recorded", "file", "notice.wav",
            LiveTest.BuildWav(16000, 16000), ("target", target));
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        return (await app.Json(result)).Str("announcement_id");
    }

    static async Task Complete(TestApp app, string id) => Assert.Equal(HttpStatusCode.OK,
        (await app.Post($"/api/history/{id}/correct", new { text = "The updated announcement transcript." })).StatusCode);

    [Fact]
    public async Task Notify_sends_latest_text_to_original_capable_audience_without_audio_or_new_jobs()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var supported = await LiveTest.EnrollAsync(app, "READY");
        var old = await LiveTest.EnrollAsync(app, "OLD-CLIENT");
        await LiveTest.EnrollAsync(app, "OFFLINE");
        await using var ready = await ControlClient.ConnectAsync(app, supported, ["transcript_notify"]);
        await using var legacy = await ControlClient.ConnectAsync(app, old);
        var id = await Recording(app);
        await Complete(app, id);
        // A computer enrolled later is outside the frozen audience.
        var later = await LiveTest.EnrollAsync(app, "LATER");
        await using var newcomer = await ControlClient.ConnectAsync(app, later, ["transcript_notify"]);
        using var conn = LiveTest.DbOf(app).Open();
        long outbox = Convert.ToInt64(conn.Scalar("SELECT COUNT(*) FROM outbox"));
        long jobs = Convert.ToInt64(conn.Scalar("SELECT COUNT(*) FROM transcript_jobs"));
        var result = await app.Post($"/api/history/{id}/notify", new { });
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        var counts = await app.Json(result);
        Assert.Equal(1, counts.Int("sent"));
        Assert.Equal(1, counts.Int("offline"));
        Assert.Equal(1, counts.Int("needs_client_update"));
        ControlEnvelope? notice = null;
        for (int i = 0; i < 20; i++)
        {
            var message = await ready.NextAsync(5000);
            Assert.NotNull(message);
            if (message!.Type == Protocol.TTranscriptNotify) { notice = message; break; }
        }
        Assert.NotNull(notice);
        Assert.Null(notice!.EventId);
        var payload = PayloadSerializer.ToPayload<TranscriptNotificationPayload>(notice)!;
        Assert.Equal(id, payload.Item.AnnouncementId);
        Assert.Equal("The updated announcement transcript.", payload.Item.Text);
        Assert.True(payload.Item.Corrected);
        Assert.True(payload.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(outbox, Convert.ToInt64(conn.Scalar("SELECT COUNT(*) FROM outbox")));
        Assert.Equal(jobs, Convert.ToInt64(conn.Scalar("SELECT COUNT(*) FROM transcript_jobs")));
        Assert.Equal(0, Convert.ToInt32(conn.Scalar("SELECT COUNT(*) FROM media_tokens")));
        Assert.Equal("active", app.Factory.Services.GetRequiredService<AnnouncementsService>().Read(id)!.Status);
    }

    [Fact]
    public async Task Notify_requires_permission_ready_text_and_schoolwide_scope()
    {
        using var app = new TestApp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Post("/api/history/missing/notify", new { })).StatusCode);
        await app.LoginAsAdmin();
        var dev = await LiveTest.EnrollAsync(app, "CLASSROOM");
        Assert.Equal(HttpStatusCode.NotFound, (await app.Post("/api/history/missing/notify", new { })).StatusCode);
        var id = await Recording(app);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Post($"/api/history/{id}/notify", new { })).StatusCode);
        await Complete(app, id);
        var targeted = await Recording(app, "{\"devices\":[\"" + dev.Id + "\"]}");
        await Complete(app, targeted);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Post($"/api/history/{targeted}/notify", new { })).StatusCode);
        await app.CreateUser("notify-viewer", "password", "viewer");
        await app.CreateUser("notify-operator", "password", "operator", schoolwide: false);
        using var viewer = new TestApp.Session(app);
        await viewer.LoginAs("notify-viewer", "password");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post($"/api/history/{id}/notify", new { })).StatusCode);
        using var op = new TestApp.Session(app);
        await op.LoginAs("notify-operator", "password");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Post($"/api/history/{id}/notify", new { })).StatusCode);
    }
}

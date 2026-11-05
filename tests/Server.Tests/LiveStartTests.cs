using System.Net;
using SchoolBell.Contracts;

namespace Server.Tests;

/// Hold to talk: the stream starts as soon as every connected computer is ready, not after the
/// full preparation window (field report: a long hold before anything was transmitted).
public class LiveStartTests
{
    static async Task<string> StartLiveAsync(TestApp app)
    {
        var lease = await app.Json(await app.Post("/api/announcements/lease", new { max_duration_s = 60, target = new { all = true } }));
        var res = await app.Post("/api/announcements/live", new { target = new { all = true }, chime = false, lease_id = lease.Str("lease_id") });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await app.Json(res)).Str("announcement_id");
    }

    static async Task ReadyAsync(ControlClient c, string announcementId)
    {
        for (int i = 0; i < 20; i++)
        {
            var e = await c.NextAsync(3000);
            Assert.NotNull(e);
            if (e!.Type == Protocol.TAnnouncementOffer) break;
        }
        await c.SendAsync(ControlEnvelope.Create(Protocol.TAnnouncementReady,
            PayloadSerializer.Node(new AnnouncementReadyPayload(announcementId, "Speakers"))));
    }

    static async Task<(long CreatedAt, long? StartedAt)> WaitStartedAsync(TestApp app, string id, int maxMs)
    {
        var a = await LiveTest.PollJsonAsync(app, $"/api/announcements/{id}",
            j => j.Prop("started_at").ValueKind == System.Text.Json.JsonValueKind.Number, maxMs);
        var started = a.Prop("started_at");
        return (a.Long("created_at"), started.ValueKind == System.Text.Json.JsonValueKind.Number ? started.GetInt64() : null);
    }

    [Fact]
    public async Task Starts_as_soon_as_every_connected_computer_is_ready_and_offline_ones_do_not_hold_it()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var a = await LiveTest.EnrollAsync(app, "RHS-A");
        var b = await LiveTest.EnrollAsync(app, "RHS-B");
        await LiveTest.EnrollAsync(app, "RHS-OFF");                // enrolled, never connected
        await using var ca = await ControlClient.ConnectAsync(app, a);
        await using var cb = await ControlClient.ConnectAsync(app, b);

        var id = await StartLiveAsync(app);
        await ReadyAsync(ca, id);
        await ReadyAsync(cb, id);

        var (created, started) = await WaitStartedAsync(app, id, 3000);
        Assert.NotNull(started);
        Assert.True(started!.Value - created < 2500, $"started {started - created} ms after the press (window is 5000 ms)");

        // both ready computers get their media token; the offline one is reported, not waited for
        Assert.Contains(await ca.DrainAsync(800), e => e.Type == Protocol.TMediaToken);
        Assert.Contains(await cb.DrainAsync(800), e => e.Type == Protocol.TMediaToken);
        var view = await app.Json(await app.Get($"/api/announcements/{id}"));
        Assert.Contains("offer_expired", view.ToString());
    }

    [Fact]
    public async Task A_connected_computer_that_stays_silent_holds_the_start_until_the_window_ends()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var a = await LiveTest.EnrollAsync(app, "RHS-A");
        var silent = await LiveTest.EnrollAsync(app, "RHS-SILENT");
        await using var ca = await ControlClient.ConnectAsync(app, a);
        await using var cs = await ControlClient.ConnectAsync(app, silent);
        LiveTest.SetSetting(app, "preparation_window_s", "2");

        var id = await StartLiveAsync(app);
        await ReadyAsync(ca, id);
        await Task.Delay(700);
        var early = await app.Json(await app.Get($"/api/announcements/{id}"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, early.Prop("started_at").ValueKind);

        var (created, started) = await WaitStartedAsync(app, id, 5000);
        Assert.NotNull(started);
        Assert.True(started!.Value - created >= 1900, $"started after {started - created} ms");
        Assert.Contains("ready_timeout", (await app.Json(await app.Get($"/api/announcements/{id}"))).ToString());
    }
}

using System.Net;
using System.Net.WebSockets;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

/// Field (2026-11-05): computers reconnected every couple of seconds after a replay. The server
/// closed the connection on manifest.rejected (an "unknown" message), and every reconnect replayed
/// the whole outbox because the live settings message moved the device's cursor out of it.
public class ResyncTests
{
    static async Task PublishAsync(TestApp app, string start)
    {
        await app.Post("/api/schedules/publish", null);
    }

    static object Schedule(string name, string start) => new
    {
        name, target = new { all = true }, weekdays = new[] { 0, 1, 2, 3, 4, 5, 6 },
        periods = new[] { new { name = "P", start_hm = start, end_hm = "23:59", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
    };

    [Fact]
    public async Task A_refused_old_schedule_does_not_drop_the_connection()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var dev = await LiveTest.EnrollAsync(app, "RHS-12328");
        await using var c = await ControlClient.ConnectAsync(app, dev);
        await c.SendAsync(ControlEnvelope.Create(Protocol.TManifestRejected,
            PayloadSerializer.Node(new ManifestRejectedPayload(1, "revision_not_newer"))));
        // the connection is still there: a ring reaches it
        Assert.Equal(HttpStatusCode.Created, (await app.Post("/api/bells/ring", new { target = new { all = true } })).StatusCode);
        var got = await c.DrainAsync(2000);
        Assert.Contains(got, e => e.Type == Protocol.TBellRing);
        Assert.Equal(WebSocketState.Open, c.Socket.State);
    }

    [Fact]
    public async Task Settings_on_connect_carry_no_event_id_and_a_replay_sends_only_the_newest_schedule()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var dev = await LiveTest.EnrollAsync(app, "RHS-12319");   // enrolled before both publishes: both queued
        await app.Post("/api/schedules", Schedule("A", "08:00"));
        await app.Post("/api/schedules/publish", null);
        await app.Post("/api/schedules", Schedule("B", "09:00"));
        var latest = (await app.Json(await app.Post("/api/schedules/publish", null))).GetProperty("revision").GetInt32();

        await using var c = await ControlClient.ConnectAsync(app, dev);
        await c.SendAsync(ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload("cursor-the-server-never-issued", 0, []))));
        var got = await c.DrainAsync(1500);

        var settings = Assert.Single(got, e => e.Type == Protocol.TDeviceSettings);
        Assert.Null(settings.EventId);
        var manifest = Assert.Single(got, e => e.Type == Protocol.TScheduleManifest);
        Assert.Equal(latest, PayloadSerializer.ToPayload<ScheduleManifestPayload>(manifest)!.Revision);
    }

    [Fact]
    public async Task Missed_bell_reports_for_bells_before_a_computer_was_enrolled_are_dropped()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        await app.Post("/api/schedules", Schedule("Day", "00:01"));
        await app.Post("/api/schedules/publish", null);
        var past = (await app.Json(await app.Get("/api/recent-bells?past=true&limit=50"))).EnumerateArray().ToList();
        if (past.Count == 0) return;   // ran between midnight and 00:01 school time
        var bellId = past[0].Str("occurrence_id");

        var dev = await LiveTest.EnrollAsync(app, "RJH-LAB");     // enrolled now, after that bell
        await using var c = await ControlClient.ConnectAsync(app, dev);
        await c.SendAsync(ControlEnvelope.Create(Protocol.TReceipt, PayloadSerializer.Node(
            new ReceiptPayload(bellId, ReceiptState.Expired, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "missed_bell"))));
        await Task.Delay(500);

        var row = (await app.Json(await app.Get("/api/recent-bells?past=true&limit=50"))).EnumerateArray()
            .Single(b => b.Str("occurrence_id") == bellId);
        Assert.Equal(0, row.Prop("delivery").GetArrayLength());
        Assert.Equal(WebSocketState.Open, c.Socket.State);
    }
}

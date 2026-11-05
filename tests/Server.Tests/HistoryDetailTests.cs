using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

/// History > Scheduled bells: per-computer results plus the computers that never reported.
public class HistoryDetailTests
{
    [Fact]
    public async Task A_past_bell_lists_the_computers_that_never_reported_it()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        var silent = await LiveTest.EnrollAsync(app, "RHS-SILENT");
        var rang = await LiveTest.EnrollAsync(app, "RHS-RANG");
        using (var c = LiveTest.DbOf(app).Open()) c.Run("UPDATE devices SET first_seen = 0");   // enrolled long before the bell
        await app.Post("/api/schedules", new
        {
            name = "Early", target = new { all = true }, weekdays = new[] { 0, 1, 2, 3, 4, 5, 6 },
            periods = new[] { new { name = "Zero bell", start_hm = "00:01", end_hm = "00:02", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
        });
        await app.Post("/api/schedules/publish", null);
        var past = (await app.Json(await app.Get("/api/recent-bells?past=true&limit=50"))).EnumerateArray().ToList();
        if (past.Count == 0) return;   // the test ran between midnight and 00:01 school time
        var bellId = past[0].Str("occurrence_id");

        await using var client = await ControlClient.ConnectAsync(app, rang);
        await client.SendAsync(ControlEnvelope.Create(Protocol.TReceipt, PayloadSerializer.Node(
            new ReceiptPayload(bellId, ReceiptState.Completed, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "played_on_retry_1"))));

        var bell = await LiveTest.PollJsonAsync(app, "/api/recent-bells?past=true&limit=50",
            j => j.EnumerateArray().Any(b => b.Str("occurrence_id") == bellId && b.Prop("delivery").GetArrayLength() > 0));
        var row = bell.EnumerateArray().Single(b => b.Str("occurrence_id") == bellId);
        var noReport = row.Prop("no_report").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(noReport, l => l.Contains("(RHS-SILENT)"));
        Assert.DoesNotContain(noReport, l => l.Contains("(RHS-RANG)"));
        var delivered = row.Prop("delivery")[0];
        Assert.Equal("completed", delivered.Str("state"));
        Assert.Equal("played_on_retry_1", delivered.Str("error_code"));
    }
}

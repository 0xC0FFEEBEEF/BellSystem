using System.Net;
using SchoolBell.Contracts;

namespace Server.Tests;

/// Admin > Log: view, verbose switch, .txt download, and the lines that explain missed bells.
public class ServerLogTests
{
    [Fact]
    public async Task Log_is_admin_only_and_downloads_as_text()
    {
        using var app = new TestApp(tz: "America/Chicago");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendBare("GET", "/api/admin/logs", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendBare("GET", "/api/admin/logs.txt", null)).StatusCode);
        await app.LoginAsAdmin();

        var on = await app.Json(await app.Post("/api/admin/logs/verbose", new { on = true }));
        Assert.True(on.GetProperty("verbose").GetBoolean());
        var view = await app.Json(await app.Get("/api/admin/logs?lines=50"));
        Assert.True(view.GetProperty("verbose").GetBoolean());
        Assert.Contains(view.GetProperty("lines").EnumerateArray(), l => l.GetString()!.Contains("verbose logging turned on by"));

        var res = await app.Get("/api/admin/logs.txt?days=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/plain", res.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".txt", res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        var text = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("SchoolBell ", text);
        Assert.Contains("verbose logging turned on by", text);

        var off = await app.Json(await app.Post("/api/admin/logs/verbose", new { on = false }));
        Assert.False(off.GetProperty("verbose").GetBoolean());
    }

    [Fact]
    public async Task A_missed_bell_is_logged_with_the_computer_and_the_bell()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var dev = await LiveTest.EnrollAsync(app, "RHS-17151");
        await using var client = await ControlClient.ConnectAsync(app, dev);
        var ring = await app.Json(await app.Post("/api/bells/ring", new { target = new { all = true }, bell_name = "Fire drill test" }));
        var ringId = ring.GetProperty("ring_id").GetString()!;
        await client.SendAsync(ControlEnvelope.Create(Protocol.TReceipt, PayloadSerializer.Node(
            new ReceiptPayload(ringId, ReceiptState.Expired, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "ring_expired"))));

        string? line = null;
        for (int i = 0; i < 50 && line is null; i++)
        {
            var view = await app.Json(await app.Get("/api/admin/logs?lines=200&q=Fire%20drill"));
            line = view.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).FirstOrDefault(l => l.Contains("-> expired"));
            if (line is null) await Task.Delay(100);
        }
        Assert.NotNull(line);
        Assert.Contains("RHS-17151", line);
        Assert.Contains("manual bell 'Fire drill test'", line);
        Assert.Contains("(ring_expired)", line);

        var connected = await app.Json(await app.Get("/api/admin/logs?lines=200&q=connected"));
        Assert.Contains(connected.GetProperty("lines").EnumerateArray(), l => l.GetString()!.Contains("(RHS-17151) connected"));
    }

    [Fact]
    public async Task Log_times_are_in_the_school_timezone_and_a_dropped_connection_is_not_an_error()
    {
        using var app = new TestApp(tz: "Asia/Tokyo");                 // +09:00, unlike the server clock
        var dev = await LiveTest.EnrollAsync(app, "RHS-DROP");
        await Task.Delay(2_500);                                          // past the startup retry
        var client = await ControlClient.ConnectAsync(app, dev);
        client.Socket.Abort();                                            // gone without a close handshake

        List<string> lines = [];
        for (int i = 0; i < 50; i++)
        {
            lines = (await app.Json(await app.Get("/api/admin/logs?lines=300"))).GetProperty("lines")
                .EnumerateArray().Select(l => l.GetString()!).ToList();
            if (lines.Any(l => l.Contains("(RHS-DROP) disconnected"))) break;
            await Task.Delay(100);
        }
        var connected = Assert.Single(lines, l => l.Contains("(RHS-DROP) connected"));
        Assert.Contains(" +09:00 ", connected);
        Assert.Contains(lines, l => l.Contains("(RHS-DROP) disconnected"));
        var errors = lines.Where(l => l.Contains(" ERROR ") || l.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}

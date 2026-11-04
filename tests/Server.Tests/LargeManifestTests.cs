using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

/// A real school day (dozens of periods, start and end bells, 7 days) makes a schedule manifest
/// far larger than the 32 KB control-message cap that applies to messages FROM devices.
public class LargeManifestTests
{
    static readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(30));

    static string Hm(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    /// 40 periods a day, start + end bell each: 400 occurrences over the 5 school days.
    static async Task<int> PublishLargeScheduleAsync(TestApp app)
    {
        await app.LoginAsAdmin();
        var periods = Enumerable.Range(0, 40).Select(i => new
        {
            name = $"Period {i + 1}", start_hm = Hm(7 * 60 + i * 12), end_hm = Hm(7 * 60 + i * 12 + 10),
            warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3,
        }).ToArray();
        var created = await app.Post("/api/schedules", new
        {
            name = "High School", target = new { all = true }, periods, weekdays = new[] { 1, 2, 3, 4, 5 },
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var published = await app.Json(await app.Post("/api/schedules/publish", null));
        return published.GetProperty("revision").GetInt32();
    }

    static async Task<(string Id, string Secret)> EnrollAsync(TestApp app, string name)
    {
        var tok = (await app.Json(await app.Post("/api/admin/enrollment-tokens",
            new { label = "big-" + name, ttl_hours = 24, max_uses = 10 }))).GetProperty("token").GetString()!;
        var res = await app.SendBare("POST", "/api/enroll", new { token = tok, device_name = name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await app.Json(res);
        return (json.GetProperty("device_id").GetString()!, json.GetProperty("device_secret").GetString()!);
    }

    /// Raw reader with no size cap of its own, so the test sees exactly what the server sends.
    static async Task<string?> ReceiveRawAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer, ct);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    static Task SendAsync(WebSocket ws, ControlEnvelope env) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(env.ToJson()), WebSocketMessageType.Text, true, Timeout.Token);

    static async Task<WebSocket> ConnectAsync(TestApp app, (string Id, string Secret) dev, bool legacyClient = false)
    {
        var ws = await app.Factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/control"), Timeout.Token);
        var challenge = JsonNode.Parse((await ReceiveRawAsync(ws, Timeout.Token))!)!;
        var nonce = challenge["payload"]!["nonce"]!.GetValue<string>();
        await SendAsync(ws, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(legacyClient
                ? new AuthResponsePayload(dev.Id, "0.9.4", AuthProof.Compute(dev.Secret, nonce, dev.Id))
                : new AuthResponsePayload(dev.Id, "0.9.5", AuthProof.Compute(dev.Secret, nonce, dev.Id), Protocol.MaxServerMessageBytes))));
        var ok = JsonNode.Parse((await ReceiveRawAsync(ws, Timeout.Token))!)!;
        Assert.Equal(Protocol.TAuthOk, ok["type"]!.GetValue<string>());
        return ws;
    }

    /// Collects raw messages until one of the wanted type arrives or the window closes.
    static async Task<(string? Match, List<string> Types)> WaitForTypeAsync(WebSocket ws, string type, int ms)
    {
        var types = new List<string>();
        using var cts = new CancellationTokenSource(ms);
        try
        {
            while (true)
            {
                var raw = await ReceiveRawAsync(ws, cts.Token);
                if (raw is null) { types.Add("(closed)"); return (null, types); }
                var t = JsonNode.Parse(raw)!["type"]!.GetValue<string>();
                types.Add($"{t} ({raw.Length} bytes)");
                if (t == type) return (raw, types);
            }
        }
        catch (OperationCanceledException) { return (null, types); }
    }

    [Fact]
    public async Task Large_schedule_manifest_is_delivered_on_resync()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var revision = await PublishLargeScheduleAsync(app);
        var dev = await EnrollAsync(app, "rhs-big1");

        using var ws = await ConnectAsync(app, dev);
        await SendAsync(ws, ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var (raw, types) = await WaitForTypeAsync(ws, Protocol.TScheduleManifest, 4000);

        Assert.True(raw is not null, "no schedule.manifest arrived; got: " + string.Join(", ", types));
        Assert.True(raw!.Length > Protocol.MaxControlMessageBytes, $"test schedule is only {raw.Length} bytes");
        var env = ControlEnvelope.Parse(raw, Protocol.MaxServerMessageBytes)!;
        var manifest = PayloadSerializer.ToPayload<ScheduleManifestPayload>(env)!;
        Assert.Equal(revision, manifest.Revision);
        Assert.Equal(400, manifest.Occurrences.Count);
    }

    [Fact]
    public async Task Unaccepted_large_manifest_is_redelivered_after_a_reconnect()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var revision = await PublishLargeScheduleAsync(app);
        var dev = await EnrollAsync(app, "rhs-big2");

        // first connection: the manifest arrives, the device drops before acking it
        var ws1 = await ConnectAsync(app, dev);
        await SendAsync(ws1, ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var (first, _) = await WaitForTypeAsync(ws1, Protocol.TScheduleManifest, 4000);
        Assert.NotNull(first);
        ws1.Abort();
        await Task.Delay(300);

        // reconnect with the same (unadvanced) cursor: it must come again, or the device never gets a schedule
        using var ws2 = await ConnectAsync(app, dev);
        await SendAsync(ws2, ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var (again, types) = await WaitForTypeAsync(ws2, Protocol.TScheduleManifest, 4000);
        Assert.True(again is not null, "manifest not redelivered; got: " + string.Join(", ", types));
        var manifest = PayloadSerializer.ToPayload<ScheduleManifestPayload>(ControlEnvelope.Parse(again!, Protocol.MaxServerMessageBytes)!)!;
        Assert.Equal(revision, manifest.Revision);
    }

    [Fact]
    public async Task Older_client_is_not_sent_an_oversized_manifest_keeps_its_connection_and_is_flagged()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await PublishLargeScheduleAsync(app);
        var dev = await EnrollAsync(app, "rhs-old1");

        // a 0.9.4 client (no max_message_bytes) would drop its connection on anything over 32 KB
        using var ws = await ConnectAsync(app, dev, legacyClient: true);
        await SendAsync(ws, ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var (manifest, types) = await WaitForTypeAsync(ws, Protocol.TScheduleManifest, 1500);
        Assert.Null(manifest);
        Assert.DoesNotContain(types, t => t.StartsWith("(closed)"));

        // small events (ring now, update offers) still get through on the same connection
        var ring = await app.Post("/api/bells/ring", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Created, ring.StatusCode);
        var (rang, after) = await WaitForTypeAsync(ws, Protocol.TBellRing, 4000);
        Assert.True(rang is not null, "bell.ring did not arrive; got: " + string.Join(", ", after));

        var acceptance = await app.Json(await app.Get("/api/schedules/acceptance"));
        var row = acceptance.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("device_id").GetString() == dev.Id);
        Assert.True(row.GetProperty("needs_client_update").GetBoolean());
        Assert.Equal("0.9.4", row.GetProperty("client_version").GetString());
    }

    [Fact]
    public async Task Current_client_accepting_the_large_manifest_is_not_flagged()
    {
        using var app = new TestApp(tz: "America/Chicago");
        var revision = await PublishLargeScheduleAsync(app);
        var dev = await EnrollAsync(app, "rhs-new1");
        using var ws = await ConnectAsync(app, dev);
        await SendAsync(ws, ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var (raw, _) = await WaitForTypeAsync(ws, Protocol.TScheduleManifest, 4000);
        Assert.NotNull(raw);
        var acceptance = await app.Json(await app.Get("/api/schedules/acceptance"));
        var row = acceptance.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("device_id").GetString() == dev.Id);
        Assert.False(row.GetProperty("needs_client_update").GetBoolean());

        var manifest = PayloadSerializer.ToPayload<ScheduleManifestPayload>(ControlEnvelope.Parse(raw!, Protocol.MaxServerMessageBytes)!)!;
        await SendAsync(ws, ControlEnvelope.Create(Protocol.TScheduleAck,
            PayloadSerializer.Node(new ScheduleAckPayload(revision, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), manifest.ExpiresAt))));
        for (int i = 0; i < 50; i++)
        {
            acceptance = await app.Json(await app.Get("/api/schedules/acceptance"));
            row = acceptance.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("device_id").GetString() == dev.Id);
            if (row.GetProperty("accepted_revision").GetInt32() == revision) break;
            await Task.Delay(100);
        }
        Assert.Equal(revision, row.GetProperty("accepted_revision").GetInt32());
        Assert.False(row.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Publishing_a_schedule_too_large_for_endpoints_is_refused_with_a_reason()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        var periods = Enumerable.Range(0, 330).Select(i => new
        {
            name = $"P{i}", start_hm = Hm(10 + i * 4), end_hm = Hm(12 + i * 4),
            warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3,
        }).ToArray();
        var created = await app.Post("/api/schedules", new
        {
            name = "Everything", target = new { all = true }, periods, weekdays = new[] { 0, 1, 2, 3, 4, 5, 6 },
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var res = await app.Post("/api/schedules/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("more than endpoints accept", await res.Content.ReadAsStringAsync());
        var acceptance = await app.Json(await app.Get("/api/schedules/acceptance"));
        Assert.Equal(0, acceptance.GetProperty("latest_revision").GetInt32());
    }
}

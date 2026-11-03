using SchoolBell.Server;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SchoolBell.Contracts;

namespace Server.Tests;

public class ControlWsTests
{
    static readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(15));

    /// Reads an HTTP endpoint repeatedly until settled(json) is true (or 5 s pass).
    /// WS messages are processed on the server's read loop; an immediate GET can race them.
    static bool Eq(JsonElement e, int v) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var x) && x == v;
    static bool Eq(JsonElement e, bool v) => e.ValueKind == (v ? JsonValueKind.True : JsonValueKind.False);
    static bool Eq(JsonElement e, string v) => e.ValueKind == JsonValueKind.String && e.GetString() == v;

    static async Task<JsonElement> PollJson(TestApp app, string path, Func<JsonElement, bool> settled, int maxMs = 5000)
    {
        while (true)
        {
            var json = await app.Json(await app.Get(path));
            if (settled(json) || maxMs <= 0) return json;
            await Task.Delay(100);
            maxMs -= 100;
        }
    }

    record Device(string Id, string Secret, string Pin);

    static async Task<Device> EnrollDeviceAsync(TestApp app, string name)
    {
        await app.LoginAsAdmin();
        var tok = (await app.Json(await app.Post("/api/admin/enrollment-tokens",
            new { label = "ws-" + name, ttl_hours = 24, max_uses = 10 }))).GetProperty("token").GetString()!;
        var res = await app.SendBare("POST", "/api/enroll", new { token = tok, device_name = name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await app.Json(res);
        return new Device(json.GetProperty("device_id").GetString()!,
            json.GetProperty("device_secret").GetString()!,
            json.GetProperty("server_trust_pin").GetString()!);
    }

    static async Task<WebSocket> ConnectAsync(TestApp app) =>
        await app.Factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/control"), Timeout.Token);

    static async Task<ControlEnvelope?> ReadEnvelopeAsync(WebSocket ws)
    {
        var buffer = new byte[32 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer, Timeout.Token);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            if (res.EndOfMessage) break;
        }
        return ControlEnvelope.Parse(sb.ToString());
    }

    static Task SendEnvelopeAsync(WebSocket ws, ControlEnvelope env) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(env.ToJson()), WebSocketMessageType.Text, true, Timeout.Token);

    [Fact]
    public async Task Handshake_auth_ok_then_resync_delivers_signed_manifest_and_ack_updates_acceptance()
    {
        using var app = new TestApp(tz: "America/Chicago");
        // publish first so a schedule.manifest event exists in the outbox
        await app.LoginAsAdmin();
        await app.Post("/api/schedules", new
        {
            name = "Regular",
            target = new { all = true },
            periods = new[] { new { name = "Period 1", start_hm = "08:30", end_hm = "09:20", warn_min = 5, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
            weekdays = new[] { 1, 2, 3, 4, 5 },
        });
        var publishJson = await app.Json(await app.Post("/api/schedules/publish", null));
        var revision = publishJson.GetProperty("revision").GetInt32();

        var device = await EnrollDeviceAsync(app, "rhs-ws1");

        // reconnect fresh: challenge+response again, then resync
        var ws = await ConnectAsync(app);
        var challenge = await ReadEnvelopeAsync(ws);
        var nonce = challenge!.Payload!["nonce"]!.GetValue<string>();
        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(device.Id, "test-client", AuthProof.Compute(device.Secret, nonce, device.Id)))));
        var ok = await ReadEnvelopeAsync(ws);
        Assert.Equal(Protocol.TAuthOk, ok!.Type);
        Assert.True(ok.Payload!["server_time"].GetValue<long>() > 0);

        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var manifest = await ReadEnvelopeAsync(ws);
        Assert.Equal(Protocol.TScheduleManifest, manifest!.Type);
        var payload = PayloadSerializer.ToPayload<ScheduleManifestPayload>(manifest)!;
        Assert.Equal(revision, payload.Revision);
        Assert.True(ManifestSigner.Verify(payload, ManifestSigner.DeriveDeviceVerifyKey(device.Pin)));

        // device acks the revision
        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TScheduleAck,
            PayloadSerializer.Node(new ScheduleAckPayload(payload.Revision, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload.ExpiresAt))));

        var acceptance = await PollJson(app, "/api/schedules/acceptance",
            j => j.GetProperty("devices").EnumerateArray().Any(d =>
                d.GetProperty("device_id").GetString() == device.Id &&
                Eq(d.GetProperty("accepted_revision"), revision)));
        var row = acceptance.GetProperty("devices").EnumerateArray()
            .First(d => d.GetProperty("device_id").GetString() == device.Id);
        Assert.Equal(revision, row.GetProperty("accepted_revision").GetInt32());
        Assert.False(row.GetProperty("stale").GetBoolean());

        ws.Dispose();
    }

    [Fact]
    public async Task Bad_proof_and_unknown_and_revoked_devices_close_with_policy_violation()
    {
        using var app = new TestApp();
        var device = await EnrollDeviceAsync(app, "rhs-ws2");

        // bad proof
        var ws1 = await ConnectAsync(app);
        var challenge = await ReadEnvelopeAsync(ws1);
        var nonce = challenge!.Payload!["nonce"]!.GetValue<string>();
        await SendEnvelopeAsync(ws1, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(device.Id, "t", new string('0', 64)))));
        var closeMsg = await ReadEnvelopeAsync(ws1);
        Assert.Null(closeMsg); // stream ended with a close frame
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ws1.CloseStatus);
        Assert.Equal("authentication failed", ws1.CloseStatusDescription);

        // unknown device id
        var ws2 = await ConnectAsync(app);
        var challenge2 = await ReadEnvelopeAsync(ws2);
        var nonce2 = challenge2!.Payload!["nonce"]!.GetValue<string>();
        await SendEnvelopeAsync(ws2, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(Guid.NewGuid().ToString(), "t", AuthProof.Compute(device.Secret, nonce2, "x")))));
        Assert.Null(await ReadEnvelopeAsync(ws2));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ws2.CloseStatus);

        // revoked device
        await app.Post("/api/admin/devices/revoke", new { device_id = device.Id });
        var ws3 = await ConnectAsync(app);
        var challenge3 = await ReadEnvelopeAsync(ws3);
        var nonce3 = challenge3!.Payload!["nonce"]!.GetValue<string>();
        await SendEnvelopeAsync(ws3, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(device.Id, "t", AuthProof.Compute(device.Secret, nonce3, device.Id)))));
        Assert.Null(await ReadEnvelopeAsync(ws3));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ws3.CloseStatus);
        Assert.Equal("revoked", ws3.CloseStatusDescription);
    }

    [Fact]
    public async Task Oversized_message_closes_with_message_too_big()
    {
        using var app = new TestApp();
        var device = await EnrollDeviceAsync(app, "rhs-ws3");
        var ws = await ConnectAsync(app);
        var challenge = await ReadEnvelopeAsync(ws);
        var nonce = challenge!.Payload!["nonce"]!.GetValue<string>();
        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(device.Id, "t", AuthProof.Compute(device.Secret, nonce, device.Id)))));
        Assert.Equal(Protocol.TAuthOk, (await ReadEnvelopeAsync(ws))!.Type);

        var huge = Encoding.UTF8.GetBytes(new string('x', 40 * 1024));
        try { await ws.SendAsync(huge, WebSocketMessageType.Text, true, Timeout.Token); }
        catch (WebSocketException) { /* server may already have closed */ }
        try { await ws.ReceiveAsync(new byte[1024], Timeout.Token); }
        catch (WebSocketException) { }
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, ws.CloseStatus);
    }

    [Fact]
    public async Task Live_keepawake_push_status_derived_states_and_keepawake_ack()
    {
        using var app = new TestApp();
        var device = await EnrollDeviceAsync(app, "rhs-ws4");
        var ws = await ConnectAsync(app);
        await HandshakeRawAsync(ws, device);
        var ok = await ReadEnvelopeAsync(ws);
        Assert.Equal(Protocol.TAuthOk, ok!.Type);

        // live push: admin creates a keep-awake policy while the device is connected
        var ka = await app.Post("/api/admin/keepawake", new { scope = "all", duration_minutes = 30 });
        Assert.Equal(HttpStatusCode.Created, ka.StatusCode);
        var evt = await ReadEnvelopeAsync(ws);
        Assert.Equal(Protocol.TKeepAwakeSet, evt!.Type);
        var policyId = evt.Payload!["policy_id"]!.GetValue<string>();

        // device acknowledges
        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TKeepAwakeAck,
            PayloadSerializer.Node(new KeepAwakeAckPayload(policyId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true))));

        var list = await PollJson(app, "/api/admin/keepawake", j => j.EnumerateArray().Any(p =>
            p.GetProperty("policy").GetProperty("policy_id").GetString() == policyId &&
            Eq(p.GetProperty("ack_count"), 1)));
        var policy = list.EnumerateArray().First(p => p.GetProperty("policy").GetProperty("policy_id").GetString() == policyId);
        Assert.Equal(1, policy.GetProperty("ack_count").GetInt32());
        Assert.Equal(1, policy.GetProperty("desired_count").GetInt32());

        // status report -> derived dashboard states on the office listing
        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TDeviceStatus,
            PayloadSerializer.Node(Status("rhs-ws4", "james.doe", renderer: "ready", outputPresent: true, muted: false))));
        var devices = await PollJson(app, "/api/devices?q=rhs-ws4", j =>
            Eq(j.GetProperty("items")[0].GetProperty("connected"), true) &&
            j.GetProperty("items")[0].GetProperty("state").GetString() == "ready_to_play");
        var item = devices.GetProperty("items")[0];
        Assert.Equal("james.doe (rhs-ws4)", item.GetProperty("label").GetString());
        Assert.True(item.GetProperty("connected").GetBoolean());
        Assert.Equal("ready_to_play", item.GetProperty("state").GetString());

        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TDeviceStatus,
            PayloadSerializer.Node(Status("rhs-ws4", "james.doe", renderer: "ready", outputPresent: true, muted: true))));
        devices = await PollJson(app, "/api/devices?q=rhs-ws4", j =>
            j.GetProperty("items")[0].GetProperty("state").GetString() == "muted");
        Assert.Equal("muted", devices.GetProperty("items")[0].GetProperty("state").GetString());

        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TDeviceStatus,
            PayloadSerializer.Node(Status("rhs-ws4", null, renderer: "no_renderer", outputPresent: false, muted: false))));
        devices = await PollJson(app, "/api/devices?q=rhs-ws4", j =>
            j.GetProperty("items")[0].GetProperty("state").GetString() == "no_renderer");
        Assert.Equal("No signed-in user (rhs-ws4)", devices.GetProperty("items")[0].GetProperty("label").GetString());
        Assert.Equal("no_renderer", devices.GetProperty("items")[0].GetProperty("state").GetString());

        ws.Dispose();
        // after disconnect the device is no longer connected -> stale/offline
        devices = await PollJson(app, "/api/devices?q=rhs-ws4", j =>
            Eq(j.GetProperty("items")[0].GetProperty("connected"), false));
        var state = devices.GetProperty("items")[0].GetProperty("state").GetString();
        Assert.Contains(state, new[] { "stale", "offline" });
        Assert.False(devices.GetProperty("items")[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Receipts_over_ws_are_idempotent_and_reported_in_recent_bells()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        await app.Post("/api/schedules", new
        {
            name = "Receipts Week",
            target = new { all = true },
            periods = new[] { new { name = "P1", start_hm = "08:30", end_hm = "09:20", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
            weekdays = new[] { 0, 1, 2, 3, 4, 5, 6 },
        });
        var publishJson = await app.Json(await app.Post("/api/schedules/publish", null));
        Assert.Equal(14, publishJson.GetProperty("occurrence_count").GetInt32()); // 7 days x 2

        var device = await EnrollDeviceAsync(app, "rhs-ws5");
        var ws = await ConnectAsync(app);
        await HandshakeRawAsync(ws, device);
        await ReadEnvelopeAsync(ws); // auth.ok
        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TDeviceResync,
            PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var manifestEnv = await ReadEnvelopeAsync(ws);
        var manifest = PayloadSerializer.ToPayload<ScheduleManifestPayload>(manifestEnv!)!;
        var occurrenceId = manifest.Occurrences[0].OccurrenceId;

        var receipt = ControlEnvelope.Create(Protocol.TReceipt,
            PayloadSerializer.Node(new ReceiptPayload(occurrenceId, ReceiptState.Completed, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null)));
        await SendEnvelopeAsync(ws, receipt);
        await SendEnvelopeAsync(ws, receipt); // duplicate delivery

        var bells = await PollJson(app, "/api/recent-bells?limit=100", j => j.EnumerateArray().Any(b =>
            b.GetProperty("occurrence_id").GetString() == occurrenceId &&
            Eq(b.GetProperty("has_receipts"), true)));
        var bell = bells.EnumerateArray().First(b => b.GetProperty("occurrence_id").GetString() == occurrenceId);
        Assert.True(bell.GetProperty("has_receipts").GetBoolean());
        Assert.Single(bell.GetProperty("delivery").EnumerateArray()); // idempotent: one row
        Assert.Equal("completed", bell.GetProperty("delivery")[0].GetProperty("state").GetString());
        ws.Dispose();
    }

    static async Task HandshakeRawAsync(WebSocket ws, Device device)
    {
        var challenge = await ReadEnvelopeAsync(ws);
        var nonce = challenge!.Payload!["nonce"]!.GetValue<string>();
        await SendEnvelopeAsync(ws, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(device.Id, "test-client", AuthProof.Compute(device.Secret, nonce, device.Id)))));
    }

    static DeviceStatusPayload Status(string computer, string? user, string renderer, bool outputPresent, bool muted) =>
        new(computer, user, user is null ? null : "S-1-5-21-1", renderer, null, "Speakers", outputPresent,
            muted, 40, false, 50, 0, 0, 0, new KeepAwakeStatus(false, false, 0), null, "running");
}

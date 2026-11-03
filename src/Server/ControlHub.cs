using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// Control WebSocket /ws/control (PROTOCOL.md §3): handshake auth.challenge -> auth.response
/// (10 s window, HMAC via Contracts.AuthProof) -> auth.ok; then resync (receipt ingest, bounded
/// outbox replay, manifest if device is behind), status, receipts, schedule.ack, keepawake.ack.
/// Protocol violations close with a policy-violation status. 32 KB per-message cap enforced.
public sealed class ControlHub
{
    private static readonly TimeSpan AuthWindow = TimeSpan.FromSeconds(10);

    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly TimeProvider _time;
    private readonly ControlRegistry _registry;
    private readonly OutboxService _outbox;
    private readonly DevicesService _devices;
    private readonly ReceiptsService _receipts;
    private readonly KeepAwakeService _keepAwake;
    private readonly PublishService _publish;
    private readonly ILogger<ControlHub> _log;

    /// Milestone S2 additions (optional so the S1 test harness still constructs ControlHub with
    /// its original 9 arguments). Null in service-layer tests that do not exercise announcements.
    private readonly AnnouncementsService? _announcements;
    private long _rejectedLimitReached;

    public ControlHub(Db db, ServerConfig cfg, TimeProvider time, ControlRegistry registry,
        OutboxService outbox, DevicesService devices, ReceiptsService receipts,
        KeepAwakeService keepAwake, PublishService publish, ILogger<ControlHub> log,
        AnnouncementsService? announcements = null)
    {
        _db = db; _cfg = cfg; _time = time; _registry = registry; _outbox = outbox;
        _devices = devices; _receipts = receipts; _keepAwake = keepAwake; _publish = publish; _log = log;
        _announcements = announcements;
    }

    public long RejectedAtConnectionLimit => Interlocked.Read(ref _rejectedLimitReached);

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { detail = "websocket upgrade required" }, Util.ApiJson);
            return;
        }
        // Origin check applies to browser upgrades; device clients (no Origin header) are exempt.
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (origin.Length > 0 && !Array.Exists(_cfg.AllowedWsOrigins, o => o == origin))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { detail = "origin not allowed" }, Util.ApiJson);
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

        // 1) challenge
        var nonce = Util.RandomHex(16); // 16 bytes => 32 hex chars; §3 says hex16 nonce
        var challenge = ControlEnvelope.Create(Protocol.TAuthChallenge,
            PayloadSerializer.Node(new AuthChallengePayload(nonce)));
        await ws.SendTextAsync(challenge.ToJson(), lifetime.Token);

        // 2) auth.response within 10 s
        string firstText;
        using (var authTimeout = new CancellationTokenSource(AuthWindow))
        {
            try { firstText = await ws.ReceiveTextAsync(authTimeout.Token); }
            catch (WebSocketHandler.Timeout)
            {
                await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "auth timeout");
                return;
            }
            catch (WebSocketHandler.TooBig)
            {
                await ws.CloseSafe(WebSocketCloseStatus.MessageTooBig, "control message exceeds 32 KB");
                return;
            }
        }

        if (ControlEnvelope.Parse(firstText) is not ControlEnvelope env || env.Type != Protocol.TAuthResponse)
        {
            await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "expected auth.response");
            return;
        }
        if (PayloadSerializer.ToPayload<AuthResponsePayload>(env) is not AuthResponsePayload resp
            || string.IsNullOrEmpty(resp.DeviceId) || string.IsNullOrEmpty(resp.Proof))
        {
            await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "invalid auth.response payload");
            return;
        }

        string secret; bool revoked;
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT secret, revoked FROM devices WHERE device_id = $d";
            cmd.Parameters.AddWithValue("$d", resp.DeviceId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "unknown device");
                return;
            }
            secret = r.GetString(0);
            revoked = r.GetInt32(1) == 1;
        }
        if (revoked)
        {
            await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "revoked");
            return;
        }
        if (!AuthProof.Verify(secret, nonce, resp.DeviceId, resp.Proof))
        {
            _log.LogWarning("control auth failed for device {DeviceId} (secret not logged)", resp.DeviceId);
            await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "authentication failed");
            return;
        }

        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        using (var conn = _db.Open())
            conn.Run("UPDATE devices SET last_seen = $t WHERE device_id = $d", ("$t", nowMs), ("$d", resp.DeviceId));

        // 3) auth.ok
        var ok = ControlEnvelope.Create(Protocol.TAuthOk,
            PayloadSerializer.Node(new AuthOkPayload(nowMs)));
        await ws.SendTextAsync(ok.ToJson(), lifetime.Token);

        var wconn = new ControlConnection(resp.DeviceId, ws, nowMs, _log);
        if (_registry.Register(wconn) is null)
        {
            Interlocked.Increment(ref _rejectedLimitReached);
            await ws.CloseSafe((WebSocketCloseStatus)1013, "server connection limit reached (configurable)");
            return;
        }

        var sendLoop = wconn.RunSendLoopAsync(lifetime.Token);
        try
        {
            await ReadLoopAsync(wconn, ws, lifetime.Token);
        }
        finally
        {
            _registry.Remove(wconn.DeviceId, wconn);
            lifetime.Cancel();
            wconn.Close(WebSocketCloseStatus.NormalClosure, "closing");
            try { await sendLoop; } catch { }
        }
    }

    private async Task ReadLoopAsync(ControlConnection conn, WebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && !conn.Closing)
        {
            string text;
            try { text = await ws.ReceiveTextAsync(ct); }
            catch (WebSocketHandler.Timeout) { break; }
            catch (WebSocketHandler.TooBig)
            {
                await ws.CloseSafe(WebSocketCloseStatus.MessageTooBig, "control message exceeds 32 KB");
                break;
            }
            conn.Touch(_time.GetUtcNow().ToUnixTimeMilliseconds());

            if (ControlEnvelope.Parse(text) is not ControlEnvelope env)
            {
                await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "invalid envelope");
                break;
            }
            if (!DispatchAsync(conn, env)) break;
        }
    }

    /// Returns false to end the read loop (connection closed).
    private bool DispatchAsync(ControlConnection conn, ControlEnvelope env)
    {
        var deviceId = conn.DeviceId;
        switch (env.Type)
        {
            case Protocol.TDeviceResync:
                Resync(conn, env);
                return true;
            case Protocol.TDeviceStatus:
                var status = PayloadSerializer.ToPayload<DeviceStatusPayload>(env);
                if (status is null || string.IsNullOrEmpty(status.ComputerName))
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid device.status");
                    return false;
                }
                _devices.UpdateStatus(deviceId, status, env.Payload!.ToJsonString());
                return true;
            case Protocol.TReceipt:
                var rcpt = PayloadSerializer.ToPayload<ReceiptPayload>(env);
                if (rcpt is null || _receipts.Ingest(deviceId, rcpt.OccurrenceOrAnnouncementId, rcpt.State, rcpt.At, rcpt.ErrorCode) < 0)
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid receipt");
                    return false;
                }
                // Milestone S2: a receipt naming an announcement also updates that announcement's
                // delivery results (device-reported states stay attributed to the device).
                _announcements?.OnDeviceReceipt(deviceId, rcpt.OccurrenceOrAnnouncementId, rcpt.State, rcpt.ErrorCode);
                return true;
            case Protocol.TAnnouncementReady:
                var ready = PayloadSerializer.ToPayload<AnnouncementReadyPayload>(env);
                if (ready is null || string.IsNullOrEmpty(ready.AnnouncementId))
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid announcement.ready");
                    return false;
                }
                if (_announcements is null || !_announcements.RecordReady(deviceId, ready.AnnouncementId, ready.OutputDevice))
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "announcement.ready names an unknown announcement or device");
                    return false;
                }
                return true;
            case Protocol.TAnnouncementReject:
                var reject = PayloadSerializer.ToPayload<AnnouncementRejectPayload>(env);
                if (reject is null || string.IsNullOrEmpty(reject.AnnouncementId))
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid announcement.reject");
                    return false;
                }
                if (_announcements is null || !_announcements.RecordReject(deviceId, reject.AnnouncementId, reject.Reason))
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "announcement.reject names an unknown announcement or device");
                    return false;
                }
                return true;
            case Protocol.TScheduleAck:
                var ack = PayloadSerializer.ToPayload<ScheduleAckPayload>(env);
                if (ack is null)
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid schedule.ack");
                    return false;
                }
                _devices.UpdateAck(deviceId, ack);
                Audit.Add(_db, $"device:{deviceId}", "schedule.ack", $"revision={ack.Revision}");
                return true;
            case Protocol.TKeepAwakeAck:
                var ka = PayloadSerializer.ToPayload<KeepAwakeAckPayload>(env);
                if (ka is null || string.IsNullOrEmpty(ka.PolicyId))
                {
                    conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid keepawake.ack");
                    return false;
                }
                _keepAwake.RecordAck(deviceId, ka);
                return true;
            default:
                conn.Close(WebSocketCloseStatus.PolicyViolation, $"unknown message type '{env.Type}'");
                return false;
        }
    }

    private void Resync(ControlConnection conn, ControlEnvelope env)
    {
        var payload = PayloadSerializer.ToPayload<DeviceResyncPayload>(env);
        if (payload is null)
        {
            conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid device.resync");
            return;
        }
        var deviceId = conn.DeviceId;

        // (a) ingest queued receipts, bounded by protocol
        var receipts = payload.Receipts ?? [];
        if (receipts.Count > Protocol.ResyncReceiptsMax)
        {
            _log.LogWarning("resync receipts from {DeviceId} truncated {From} -> {Max}",
                deviceId, receipts.Count, Protocol.ResyncReceiptsMax);
            receipts = receipts.TakeLast(Protocol.ResyncReceiptsMax).ToList();
        }
        foreach (var r in receipts)
            if (_receipts.Ingest(deviceId, r.OccurrenceOrAnnouncementId, r.State, r.At, r.ErrorCode) < 0)
            {
                conn.Close(WebSocketCloseStatus.PolicyViolation, "invalid queued receipt");
                return;
            }

        if (!string.IsNullOrEmpty(payload.Cursor))
            _devices.UpdateCursor(deviceId, payload.Cursor);

        // (b) replay outbox events after cursor. Milestone S2 replay filter: an expired live
        // announcement.offer is never replayed (the device is reported `expired` instead) and a
        // used/expired media token is never replayed; recorded offers replay while valid.
        var replayed = _outbox.Replay(deviceId, payload.Cursor);
        foreach (var json in replayed)
        {
            if (ControlEnvelope.Parse(json) is not ControlEnvelope e) continue;
            if (_announcements is not null && !_announcements.OutboxReplayOk(deviceId, e))
            {
                _log.LogInformation("outbox replay skipped {EventType} for device {DeviceId} (expired live offer, or a used/expired media token)",
                    e.Type, deviceId);
                continue;
            }
            if (e.Type == Protocol.TScheduleManifest)
                _log.LogDebug("outbox replay includes schedule.manifest for {DeviceId}", deviceId);
            conn.SendRaw(json);
        }

        // (c) current manifest if the device is behind and no manifest was in the replay
        var latest = _publish.LatestRevision() ?? 0;
        long knownRev;
        using (var dbConn = _db.Open())
        {
            knownRev = Math.Max(payload.CacheRevision,
                Convert.ToInt64(dbConn.Scalar("SELECT accepted_revision FROM devices WHERE device_id = $d", ("$d", deviceId)) ?? 0));
        }
        bool replayedManifest = replayed.Any(j => j.Contains($"\"type\":\"{Protocol.TScheduleManifest}\""));
        if (latest > 0 && knownRev < latest && !replayedManifest)
        {
            using var dbConn = _db.Open();
            var manifestJson = dbConn.Scalar(
                "SELECT manifest_json FROM published_revisions ORDER BY revision DESC LIMIT 1") as string;
            if (manifestJson is not null)
            {
                var manifestEnvelope = ControlEnvelope.Create(Protocol.TScheduleManifest, JsonNode.Parse(manifestJson));
                _outbox.Enqueue(deviceId, manifestEnvelope);
            }
        }

        // re-send active keep-awake policies (never expired ones)
        foreach (var policy in _keepAwake.ActivePoliciesFor(deviceId))
            if (!_keepAwake.HasPendingSet(deviceId, policy.PolicyId))
                _keepAwake.EnqueueSet(deviceId, policy);
    }
}

internal static class WebSocketHandler
{
    public sealed class Timeout : Exception { }
    public sealed class TooBig : Exception { }

    public static async Task SendTextAsync(this WebSocket ws, string text, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public static async Task CloseSafe(this WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                await ws.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    /// Aggregates fragments; enforces the 32 KB control-message cap.
    public static async Task<string> ReceiveTextAsync(this WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[Protocol.MaxControlMessageBytes];
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            WebSocketReceiveResult res;
            try { res = await ws.ReceiveAsync(buffer, ct); }
            catch (OperationCanceledException) { throw new Timeout(); }
            if (res.MessageType == WebSocketMessageType.Close) throw new Timeout();
            if (res.MessageType == WebSocketMessageType.Binary) throw new TooBig();
            sb.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, res.Count));
            if (sb.Length > Protocol.MaxControlMessageBytes) throw new TooBig();
            if (res.EndOfMessage) return sb.ToString();
        }
    }
}

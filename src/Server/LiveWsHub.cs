using System.Net.WebSockets;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// Office microphone WebSocket (/ws/mic?lease_id=...) and device media WebSocket
/// (/ws/media?announcement_id=...&token=...&device_id=...), PROTOCOL.md §7.
///
/// Office mic: cookie session + REQUIRED Origin check (browser upgrade), only the lease holder,
/// binary SBMF frames only. Every accepted frame is appended to the single server-side recording
/// and fanned out to the per-device bounded queues.
/// Device media: single-use OTP redemption, then frames only — server-assigned sequence numbers,
/// original capture timestamps preserved. Socket delivery is recorded as `started`; audible
/// completion comes only from the device's own `completed` receipt.
public sealed class LiveWsHub
{
    private readonly ServerConfig _cfg;
    private readonly LiveSettings _settings;
    private readonly TimeProvider _time;
    private readonly AnnouncementsService _announcements;
    private readonly MediaRouter _media;
    private readonly TalkLeaseService _leases;
    private readonly LiveMetrics _metrics;
    private readonly LiveTranscriptionService _transcription;
    private readonly ILogger<LiveWsHub> _log;

    public LiveWsHub(ServerConfig cfg, LiveSettings settings, TimeProvider time,
        AnnouncementsService announcements, MediaRouter media, TalkLeaseService leases, LiveMetrics metrics,
        LiveTranscriptionService transcription,
        ILogger<LiveWsHub> log)
    {
        _cfg = cfg; _settings = settings; _time = time; _announcements = announcements;
        _media = media; _leases = leases; _metrics = metrics; _transcription = transcription; _log = log;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// Browser WebSocket origins: the configured allow-list, plus loopback origins while the
    /// server itself is reached on loopback (development/lab; a LAN hostname is never implied).
    public static bool OriginAllowed(ServerConfig cfg, HttpContext ctx, string origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        if (Array.Exists(cfg.AllowedWsOrigins, o => o == origin)) return true;
        return IsLoopbackHost(ctx.Request.Host.Host) && IsLoopbackOrigin(origin);
    }

    private static bool IsLoopbackHost(string host) => host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host);

    // ------------------------------------------------------------------ office mic

    public async Task HandleMicAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { detail = "websocket upgrade required" }, Util.ApiJson);
            return;
        }

        // PROTOCOL.md §10: Origin check on browser WebSocket upgrades — required here, no bypass.
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (origin.Length == 0)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { detail = "Origin header required for the office microphone connection" }, Util.ApiJson);
            return;
        }
        if (!OriginAllowed(_cfg, ctx, origin))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { detail = "origin not allowed (SchoolBell:AllowedWsOrigins)" }, Util.ApiJson);
            return;
        }

        if (Guard.User(ctx) is not CurrentUser user)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { detail = "authentication required" }, Util.ApiJson);
            return;
        }

        var leaseId = ctx.Request.Query["lease_id"].ToString();
        if (string.IsNullOrEmpty(leaseId))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { detail = "lease_id query parameter required" }, Util.ApiJson);
            return;
        }

        var lease = _leases.Read(leaseId);
        if (lease is null)
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { detail = "no such lease" }, Util.ApiJson);
            return;
        }
        if (lease.Status != "active")
        {
            ctx.Response.StatusCode = 410;
            await ctx.Response.WriteAsJsonAsync(new { detail = $"lease expired ({lease.EndedReason ?? "expired"})" }, Util.ApiJson);
            return;
        }
        if (lease.Username != user.Username)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { detail = $"lease held by {lease.Username}" }, Util.ApiJson);
            return;
        }
        var (gate, gateDetail) = _leases.GateTarget(user, lease.Target);
        if (gate != 200)
        {
            ctx.Response.StatusCode = gate;
            await ctx.Response.WriteAsJsonAsync(new { detail = gateDetail }, Util.ApiJson);
            return;
        }

        var announcementId = lease.AnnouncementId;
        AnnouncementRow? ann = announcementId is null ? null : _announcements.Read(announcementId);
        if (announcementId is null || ann is null || ann.Status != "active" || !ann.IsLive)
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsJsonAsync(new { detail = "this lease has no open live announcement (POST api/announcements/live first)" }, Util.ApiJson);
            return;
        }

        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var mic = _media.RegisterMic(announcementId!, leaseId, ws);
        _metrics.MicConnected();
        var path = Path.Combine(_announcements.AudioDir, $"ann-{announcementId}.wav");
        using var writer = new WavWriter(path, Protocol.SampleRate);
        var guard = new FrameRateGuard(_settings.MicFramesPerS, _settings.MicBurstS, NowMs);
        long framesAccepted = 0, framesDropped = 0;
        string closeReason = "closed";
        _transcription.Start(announcementId);

        try
        {
            var buffer = new byte[Protocol.MaxMediaFrameBytes];
            while (ws.State == WebSocketState.Open && !mic.OperatorClosing)
            {
                WebSocketReceiveResult res;
                try { res = await ws.ReceiveAsync(buffer, ctx.RequestAborted); }
                catch (WebSocketException) { closeReason = "mic_socket_error"; break; }
                catch (OperationCanceledException) { closeReason = "mic_socket_error"; break; }

                if (res.MessageType == WebSocketMessageType.Close) { closeReason = "mic_closed"; break; }
                if (res.MessageType == WebSocketMessageType.Text)
                {
                    closeReason = "binary_frames_expected";
                    await ws.CloseSafe(WebSocketCloseStatus.PolicyViolation, "binary SBMF frames expected");
                    break;
                }

                var frame = MediaFrame.TryParse(buffer.AsSpan(0, res.Count));
                if (frame is null)
                {
                    // Frozen Contracts bound: payload <= 640 B (one 20 ms frame @ 16 kHz mono).
                    // A 40 ms capture must be split into two frames by the client.
                    _metrics.AddOversizeFrame();
                    _log.LogWarning("office mic sent an invalid SBMF frame ({Bytes} bytes) on announcement {AnnouncementId}: dropped",
                        res.Count, announcementId);
                    continue;
                }

                long now = NowMs;
                var current = _leases.Read(leaseId);
                if (current is null || current.Status != "active")
                {
                    closeReason = "lease_expired";
                    await ws.CloseSafe(WebSocketCloseStatus.NormalClosure, "lease expired");
                    break;
                }
                if (!guard.Allow(now))
                {
                    framesDropped++;
                    _metrics.AddDroppedFrames(1);
                    if (framesDropped % 200 == 1)
                        _log.LogWarning("flood guard on announcement {AnnouncementId}: {Dropped} frames over the {Limit}/s limit dropped so far",
                            announcementId, framesDropped, _settings.MicFramesPerS);
                    continue;
                }

                writer.Write(frame.Value.Pcm.Span);
                _transcription.Append(announcementId, frame.Value.Pcm.Span);
                _media.Broadcast(announcementId, frame.Value.Pcm, frame.Value.CaptureTsMs);
                framesAccepted++;
            }
        }
        finally
        {
            _metrics.MicDisconnected();
            _media.UnregisterMic(announcementId, mic);
            _transcription.Complete(announcementId);

            var final = writer.Finalize();
            _announcements.StoreRecording(announcementId, final, path);

            // Lease invalidated on mic close (PROTOCOL.md §7: tab closure ends the lease).
            _leases.Invalidate(leaseId, mic.OperatorClosing ? "announcement_ended" : "mic_closed");

            string endedBy = mic.OperatorClosing ? "operator" : "error";
            _announcements.End(announcementId, endedBy, user.Username);
            _log.LogInformation("office mic closed for announcement {AnnouncementId} ({Reason}): {Accepted} frames written ({Samples} samples, {Bytes} bytes), {Dropped} dropped by the flood guard, ended_by={EndedBy}",
                announcementId, closeReason, framesAccepted, final.Samples, final.Bytes, framesDropped, endedBy);
            try { await ws.CloseSafe(WebSocketCloseStatus.NormalClosure, closeReason); } catch { }
        }
    }

    // ------------------------------------------------------------------ device media

    public async Task HandleMediaAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            // Plain GET on this path is not served: /ws/media is a WebSocket media endpoint
            // (milestone S2). Kept explicit so it is never mistaken for a REST resource.
            ctx.Response.StatusCode = 501;
            await ctx.Response.WriteAsJsonAsync(new { detail = "GET /ws/media is a WebSocket media endpoint (milestone S2); a websocket upgrade is required" }, Util.ApiJson);
            return;
        }

        var origin = ctx.Request.Headers["Origin"].ToString();
        if (origin.Length > 0 && !OriginAllowed(_cfg, ctx, origin))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { detail = "origin not allowed (SchoolBell:AllowedWsOrigins)" }, Util.ApiJson);
            return;
        }

        var announcementId = ctx.Request.Query["announcement_id"].ToString();
        var token = ctx.Request.Query["token"].ToString();
        var deviceId = ctx.Request.Query["device_id"].ToString();
        if (string.IsNullOrEmpty(announcementId) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deviceId))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { detail = "announcement_id, token and device_id query parameters are required" }, Util.ApiJson);
            return;
        }

        // Atomic single-use OTP redemption bound to (device_id, announcement_id) with TTL.
        if (!_announcements.ConsumeMediaToken(announcementId, deviceId, token))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { detail = "media token rejected (unknown, wrong device, expired, or already used)" }, Util.ApiJson);
            return;
        }

        var ann = _announcements.Read(announcementId);
        if (ann is null)
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { detail = "no such announcement" }, Util.ApiJson);
            return;
        }

        if (ann.IsLive) _media.OpenStream(announcementId, deviceId);
        else if (ann.RecordingPath is not null) _media.OpenStream(announcementId, deviceId, ann.RecordingPath);

        var stream = _media.Stream(announcementId, deviceId);
        if (stream is null)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { detail = "no media stream for this device (reply announcement.ready while the announcement is open)" }, Util.ApiJson);
            return;
        }

        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        _media.AttachSocket(announcementId, deviceId, ws);

        using var cts = new CancellationTokenSource();
        var incoming = Task.Run(() => DrainIncomingAsync(ws, cts));
        bool deliveredLast = false;
        string reason = "completed";
        long staleDropped = 0;

        try
        {
            if (stream.RecordedPath is not null)
            {
                ulong baseTs = (ulong)(ann.StartedAt ?? ann.CreatedAt);
                // Real-time pacing: the device renderer keeps at most ~2 s buffered (it discards
                // backlog rather than play late), so recorded audio is sent at playback speed after
                // a short lead that primes its jitter buffer. 100 ms chunks keep timer load low.
                const int leadFrames = 20, chunkFrames = 5;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int sent = 0;
                foreach (var frame in MediaRouter.RecordedFrames(stream.RecordedPath, baseTs))
                {
                    if (cts.IsCancellationRequested || ws.State != WebSocketState.Open) { reason = "media_socket_closed"; break; }
                    if (sent >= leadFrames && (sent - leadFrames) % chunkFrames == 0)
                    {
                        long dueMs = (long)(sent - leadFrames) * Protocol.FrameMs;
                        long ahead = dueMs - clock.ElapsedMilliseconds;
                        if (ahead > 2) await Task.Delay(TimeSpan.FromMilliseconds(ahead), cts.Token);
                    }
                    await ws.SendAsync(frame.ToBytes(), WebSocketMessageType.Binary, true, cts.Token);
                    _media.CountFrame(stream);
                    sent++;
                    if (frame.IsLast) { deliveredLast = true; break; }
                }
            }
            else
            {
                int staleMs = _settings.LiveStaleFrameMs;
                while (await stream.Reader.WaitToReadAsync(cts.Token))
                {
                    while (stream.Reader.TryRead(out var frame))
                    {
                        if (ws.State != WebSocketState.Open) { reason = "media_socket_closed"; break; }
                        // A lagging device gets current speech, not a backlog: live frames older than
                        // the stale limit are dropped here (the renderer would discard them anyway).
                        if (!frame.IsLast && staleMs > 0 && NowMs - (long)frame.CaptureTsMs > staleMs)
                        {
                            staleDropped++;
                            continue;
                        }
                        await ws.SendAsync(frame.ToBytes(), WebSocketMessageType.Binary, true, cts.Token);
                        _media.CountFrame(stream);
                        if (frame.IsLast) { deliveredLast = true; break; }
                    }
                    if (deliveredLast || reason != "completed") break;
                }
                if (!deliveredLast && reason == "completed") reason = "media_socket_closed";
            }
        }
        catch (WebSocketException e)
        {
            reason = "media_socket_error";
            _log.LogWarning("device {DeviceId} media socket failed on announcement {AnnouncementId}: {Error}",
                deviceId, announcementId, e.Message);
        }
        catch (OperationCanceledException) { reason = "media_socket_closed"; }
        finally
        {
            cts.Cancel();
            _media.ReportStreamClosed(stream, deliveredLast ? "completed" : reason, deliveredLast);
            if (staleDropped > 0)
                _log.LogInformation("device {DeviceId} lagged on announcement {AnnouncementId}: {Dropped} stale live frame(s) skipped (older than {Ms} ms)",
                    deviceId, announcementId, staleDropped, _settings.LiveStaleFrameMs);
            _log.LogInformation("device {DeviceId} media stream closed for announcement {AnnouncementId}: {Sent} frame(s) written to the socket, last frame {Last} — audible completion only counts if the device reports a completed receipt",
                deviceId, announcementId, stream.FramesSent, deliveredLast);
            try { await ws.CloseSafe(deliveredLast ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable,
                deliveredLast ? "announcement stream complete" : "media stream incomplete"); } catch { }
            try { await incoming; } catch { }
        }
    }

    /// Read (and discard) inbound frames so a device closing its socket is noticed promptly and
    /// the send loop stops; a slow device therefore only slows itself.
    private static async Task DrainIncomingAsync(WebSocket ws, CancellationTokenSource cts)
    {
        var buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    cts.Cancel();
                    return;
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally { cts.Cancel(); }
    }
}

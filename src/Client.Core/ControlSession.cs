using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Client-side control WebSocket session implementing PROTOCOL §3:
/// auth.challenge -> auth.response (HMAC proof via Contracts.AuthProof) -> auth.ok,
/// then framed JSON control messages (fragment aggregation; we send at most 32 KB and accept up to
/// Protocol.MaxServerMessageBytes from the server, which we advertise in auth.response).
/// Heartbeat: ClientWebSocket sends WS pings every 10 s and answers server pings
/// automatically (pong), matching the server's Kestrel KeepAliveInterval=10 s.
/// TLS is validated normally — no RemoteCertificateValidationCallback override.
/// http:// (ws://) is permitted only for loopback development servers (ClientConfig gate).
public sealed class ControlSession : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private long _lastServerContactMs;

    public ClientConfig Config { get; }
    public AuthOkPayload? AuthOk { get; private set; }
    public long LastServerContactMs => Volatile.Read(ref _lastServerContactMs);
    public WebSocketState State => _ws.State;

    private ControlSession(ClientConfig cfg, TimeProvider time, ILogger log)
    {
        Config = cfg; _time = time; _log = log;
        _lastServerContactMs = time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    public static async Task<ControlSession> ConnectAsync(ClientConfig cfg, TimeProvider time, ILogger log, CancellationToken ct)
    {
        var session = new ControlSession(cfg, time, log);
        session._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(Protocol.HeartbeatSeconds);
        // ping/pong heartbeat: a server that stops answering for 30 s is treated as gone
        session._ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(Protocol.StaleAfterSeconds);

        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeout.Token);
        await session._ws.ConnectAsync(new Uri(cfg.ControlWsUrl), linked.Token);

        session.AuthOk = await session.HandshakeAsync(cfg, linked.Token);
        return session;
    }

    private async Task<AuthOkPayload> HandshakeAsync(ClientConfig cfg, CancellationToken ct)
    {
        // 1) expect auth.challenge
        var challenge = await ReceiveEnvelopeAsync(ct)
            ?? throw new WebSocketException("connection closed before auth.challenge");
        if (challenge.Type != Protocol.TAuthChallenge)
            throw new WebSocketException($"expected auth.challenge, got '{challenge.Type}'");
        var nonce = PayloadSerializer.ToPayload<AuthChallengePayload>(challenge)?.Nonce
            ?? throw new WebSocketException("auth.challenge missing nonce");

        // 2) auth.response (never log device_secret or proof)
        var proof = AuthProof.Compute(cfg.DeviceSecret, nonce, cfg.DeviceId);
        await SendAsync(ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(cfg.DeviceId, cfg.ClientVersion, proof,
                Protocol.MaxServerMessageBytes, ["live_transcript"]))), ct);

        // 3) auth.ok
        var ok = await ReceiveEnvelopeAsync(ct)
            ?? throw new WebSocketException("connection closed before auth.ok");
        if (ok.Type != Protocol.TAuthOk)
            throw new WebSocketException($"expected auth.ok, got '{ok.Type}'");
        var payload = PayloadSerializer.ToPayload<AuthOkPayload>(ok)
            ?? throw new WebSocketException("invalid auth.ok payload");
        _log.LogInformation("control session authenticated for device {DeviceId} (secrets not logged)", cfg.DeviceId);
        return payload;
    }

    public async Task SendAsync(ControlEnvelope env, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(env.ToJson());
        if (bytes.Length > Protocol.MaxControlMessageBytes)
            throw new InvalidOperationException("control message exceeds 32 KB");
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    /// Reads one text envelope; null when the socket closed. Enforces Protocol.MaxServerMessageBytes
    /// (a 7-day schedule manifest is often well over 100 KB).
    public async Task<ControlEnvelope?> ReceiveEnvelopeAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult res;
            try { res = await _ws.ReceiveAsync(buffer, ct); }
            catch (WebSocketException) { return null; }
            catch (OperationCanceledException) { throw; }
            if (res.MessageType == WebSocketMessageType.Close) return null;
            if (res.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException("unexpected binary frame on control channel");
            message.Write(buffer, 0, res.Count);
            if (message.Length > Protocol.MaxServerMessageBytes)
                throw new WebSocketException($"control message exceeds {Protocol.MaxServerMessageBytes / 1024} KB");
            if (res.EndOfMessage) break;
        }
        Volatile.Write(ref _lastServerContactMs, _time.GetUtcNow().ToUnixTimeMilliseconds());
        // decode once: a multi-byte character can straddle two receive chunks
        var env = ControlEnvelope.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length),
            Protocol.MaxServerMessageBytes);
        if (env is null) throw new WebSocketException("invalid control envelope");
        return env;
    }

    /// Diagnostics: how long since the last server app-level message. Note that a quiet
    /// server is NOT stale — a truly dead connection surfaces as a socket error or a
    /// failed WS ping, which aborts the session and triggers a jittered reconnect.
    public long MsSinceLastServerContact(long nowMs) => nowMs - LastServerContactMs;

    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _ws.Dispose();
        await Task.CompletedTask;
    }
}

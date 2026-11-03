using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

/// In-process fake control+media+sound server replicating the parts of the live
/// server (ControlHub/OutboxService/SoundService) that a device session depends on:
/// handshake (auth.challenge/response/ok with real HMAC verification), durable
/// outbox replay after cursor, keep-awake push, announcement offer/token/media/end,
/// forced disconnects. Only used by Client.Tests (integration tests).
public sealed class FakeControlServer : IAsyncDisposable
{
    private WebApplication _app;
    private readonly List<(long Seq, ControlEnvelope Env)> _outbox = [];
    private readonly List<Waiter> _waiters = [];
    private long _seq;
    private int _connCount;
    private WebSocket? _liveConn;
    private Channel<string> _liveChannel = Channel.CreateUnbounded<string>();
    private readonly object _gate = new();

    public string Url { get; private set; } = "";
    public string DeviceId { get; }
    public string DeviceSecretHex { get; }
    public string TrustPinHex { get; } = ManifestTestKit.TrustPinHex;
    public bool ProofVerified { get; private set; }
    public int ConnectionCount { get { lock (_gate) return _connCount; } }
    public int RudeCloseFirstConnections { get; set; } // close this many connections before auth.ok
    public List<ControlEnvelope> Received { get; } = [];
    public List<DeviceResyncPayload> Resyncs { get; } = [];
    public Dictionary<string, byte[]> Sounds { get; } = [];
    public MediaPlan? Media { get; set; }

    public sealed record MediaPlan(string AnnouncementId, string Token, List<MediaFrame> Frames, int PacingMs);

    public static async Task<FakeControlServer> StartAsync(string deviceId, string secretHex)
    {
        var server = new FakeControlServer(deviceId, secretHex);
        IOException? lastError = null;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            int port = 21000 + Random.Shared.Next(23000);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
            app.Map("/ws/control", (HttpContext ctx) => server.HandleControlAsync(ctx));
            app.Map("/ws/media", (HttpContext ctx) => server.HandleMediaAsync(ctx));
            app.Map("/api/sounds/{id}", (HttpContext ctx, string id) => server.HandleSoundAsync(ctx, id));
            try
            {
                await app.StartAsync();
                server._app = app;
                server.Url = $"http://127.0.0.1:{port}";
                return server;
            }
            catch (IOException e)
            {
                lastError = e;
                await app.DisposeAsync();
            }
        }
        throw new InvalidOperationException($"could not bind a loopback port: {lastError?.Message}");
    }

    private FakeControlServer(string deviceId, string secretHex)
    {
        DeviceId = deviceId;
        DeviceSecretHex = secretHex;
        _app = null!;
    }

    // ---------- test-facing API ----------

    /// Queue a durable event (outbox semantics: replayed after cursor on resync).
    public ControlEnvelope QueueEvent(string type, JsonNode? payload)
    {
        var env = ControlEnvelope.Create(type, payload);
        lock (_gate)
        {
            _outbox.Add((++_seq, env));
            _liveChannel.Writer.TryWrite(env.ToJson());
        }
        return env;
    }

    public void CloseLiveConnectionAbruptly()
    {
        var ws = _liveConn;
        if (ws is null) return;
        try { ws.CloseAsync((WebSocketCloseStatus)1013, "server restart", CancellationToken.None).GetAwaiter().GetResult(); }
        catch (WebSocketException) { }
    }

    public Task<ControlEnvelope> WaitForAsync(string type, int timeoutMs = 20_000)
    {
        var tcs = new TaskCompletionSource<ControlEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiters.Add(new WaitingWaiter(type, tcs));
        return WaitWithTimeoutAsync(tcs.Task, type, timeoutMs);
    }

    public Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 20_000)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (SafeCond(condition)) tcs.TrySetResult(true); // evaluate NOW: the event may already have happened
        else lock (_gate) _waiters.Add(new CondWaiter(condition, tcs));
        return WaitWithTimeoutAsync(tcs.Task, "condition", timeoutMs);
    }

    private static bool SafeCond(Func<bool> cond)
    {
        try { return cond(); } catch { return false; }
    }

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, string label, int timeoutMs)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
        if (done != task) throw new TimeoutException($"fake server timed out waiting for {label}");
        return await task;
    }

    private void NotifyWaiters(ControlEnvelope env)
    {
        foreach (var w in _waiters.ToList())
        {
            if (w.Matches(env))
            {
                _waiters.Remove(w);
                w.SetResult(env);
            }
        }
    }

    private abstract class Waiter
    {
        public abstract bool Matches(ControlEnvelope env);
        public abstract void SetResult(ControlEnvelope env);
    }

    private sealed class WaitingWaiter(string type, TaskCompletionSource<ControlEnvelope> tcs) : Waiter
    {
        public override bool Matches(ControlEnvelope env) => env.Type == type;
        public override void SetResult(ControlEnvelope env) => tcs.TrySetResult(env);
    }

    private sealed class CondWaiter(Func<bool> cond, TaskCompletionSource<bool> tcs) : Waiter
    {
        public override bool Matches(ControlEnvelope env)
        {
            try { return cond(); } catch { return false; }
        }
        public override void SetResult(ControlEnvelope env) => tcs.TrySetResult(true);
    }

    // ---------- server internals ----------

    private async Task HandleControlAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        int conn;
        lock (_gate) conn = ++_connCount;

        if (conn <= RudeCloseFirstConnections) // simulate server restart / dropped socket
        {
            try { await ws.CloseAsync((WebSocketCloseStatus)1013, "try again later", CancellationToken.None); }
            catch (WebSocketException) { }
            return;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await SendTextAsync(ws, ControlEnvelope.Create(Protocol.TAuthChallenge,
            PayloadSerializer.Node(new AuthChallengePayload(nonce))).ToJson());

        var first = await ReceiveTextAsync(ws);
        if (first is null) return;
        var env = ControlEnvelope.Parse(first);
        if (env is null || env.Type != Protocol.TAuthResponse) return;
        var resp = PayloadSerializer.ToPayload<AuthResponsePayload>(env);
        if (resp is null || resp.DeviceId != DeviceId ||
            !AuthProof.Verify(DeviceSecretHex, nonce, resp.DeviceId, resp.Proof)) return;
        ProofVerified = true;

        await SendTextAsync(ws, ControlEnvelope.Create(Protocol.TAuthOk,
            PayloadSerializer.Node(new AuthOkPayload(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))).ToJson());

        var channel = Channel.CreateUnbounded<string>();
        lock (_gate) { _liveConn = ws; _liveChannel = channel; }
        var sendLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var json in channel.Reader.ReadAllAsync())
                {
                    if (ws.State != WebSocketState.Open) break;
                    await SendTextAsync(ws, json);
                }
            }
            catch (WebSocketException) { }
        });

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(ws);
                if (text is null) break;
                var e = ControlEnvelope.Parse(text);
                if (e is null) continue;
                lock (_gate) Received.Add(e);
                if (e.Type == Protocol.TDeviceResync)
                {
                    var payload = PayloadSerializer.ToPayload<DeviceResyncPayload>(e);
                    if (payload is not null)
                    {
                        lock (_gate) Resyncs.Add(payload);
                        ReplayAfterCursor(payload.Cursor);
                    }
                }
                NotifyWaiters(e);
            }
        }
        finally
        {
            lock (_gate) if (_liveConn == ws) _liveConn = null;
            channel.Writer.TryComplete();
            try { await sendLoop; } catch { }
        }
    }

    private void ReplayAfterCursor(string? cursorEventId)
    {
        long after = 0;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(cursorEventId))
            {
                var hit = _outbox.LastOrDefault(x => x.Env.EventId == cursorEventId);
                after = hit.Env is null ? 0 : hit.Seq;
            }
            foreach (var (seq, env) in _outbox.Where(x => x.Seq > after))
                _liveChannel.Writer.TryWrite(env.ToJson());
        }
    }

    private async Task HandleMediaAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var plan = Media;
        var id = ctx.Request.Query["announcement_id"].ToString();
        var token = ctx.Request.Query["token"].ToString();
        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        if (plan is null || plan.AnnouncementId != id || plan.Token != token)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "bad media token", CancellationToken.None); }
            catch (WebSocketException) { }
            return;
        }
        try
        {
            foreach (var f in plan.Frames)
            {
                await ws.SendAsync(f.ToBytes(), WebSocketMessageType.Binary, true, CancellationToken.None);
                if (plan.PacingMs > 0) await Task.Delay(plan.PacingMs);
            }
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "media done", CancellationToken.None);
        }
        catch (WebSocketException) { }
    }

    private Task HandleSoundAsync(HttpContext ctx, string id)
    {
        if (!Sounds.TryGetValue(id, out var bytes)) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }
        ctx.Response.ContentType = "audio/wav";
        return ctx.Response.Body.WriteAsync(bytes).AsTask();
    }

    private static async Task SendTextAsync(WebSocket ws, string text) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string?> ReceiveTextAsync(WebSocket ws)
    {
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            WebSocketReceiveResult res;
            try { res = await ws.ReceiveAsync(buffer, CancellationToken.None); }
            catch (WebSocketException) { return null; }
            if (res.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            if (res.EndOfMessage) return sb.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }
}

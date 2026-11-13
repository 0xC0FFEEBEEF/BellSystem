using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

/// Shared helpers for the milestone S2 (Live*) tests: disposable test accounts/devices,
/// a control-WS client with a background reader (so envelope arrival never blocks a test),
/// an office-mic client and a device media client, plus small polling/JSON utilities.
public sealed record FakeDevice(string Id, string Secret, string Name);

public static class LiveTest
{
    public const string Password = "correct-horse99!";

    public static async Task<FakeDevice> EnrollAsync(TestApp app, string name)
    {
        await app.LoginAsAdmin();
        var tok = (await app.Json(await app.Post("/api/admin/enrollment-tokens",
            new { label = "live-" + name, ttl_hours = 24, max_uses = 50 }))).GetProperty("token").GetString()!;
        var res = await app.SendBare("POST", "/api/enroll", new { token = tok, device_name = name });
        Assert.Equal(System.Net.HttpStatusCode.Created, res.StatusCode);
        var json = await app.Json(res);
        return new FakeDevice(json.GetProperty("device_id").GetString()!, json.GetProperty("device_secret").GetString()!, name);
    }

    public static async Task<JsonElement> CreateGroupAsync(TestApp app, string name) =>
        await app.Json(await app.Post("/api/admin/groups", new { name }));

    public static async Task AddDeviceToGroupAsync(TestApp app, long groupId, string deviceId)
    {
        var res = await app.Post($"/api/admin/groups/{groupId}/members", new { device_id = deviceId });
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
    }

    public static Db DbOf(TestApp app) => app.Factory.Services.GetRequiredService<Db>();

    /// Shrink a preparation/offer/token window so a test does not sit waiting for a default 5 s window.
    public static void SetSetting(TestApp app, string key, string value) => DbOf(app).SetSetting(key, value);

    public static async Task<JsonElement> PollJsonAsync(TestApp app, string path, Func<JsonElement, bool> settled,
        int maxMs = 12_000)
    {
        while (true)
        {
            var json = await app.Json(await app.Get(path));
            if (settled(json) || maxMs <= 0) return json;
            await Task.Delay(150);
            maxMs -= 150;
        }
    }

    public static JsonElement Prop(this JsonElement e, string name) => e.GetProperty(name);
    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();
    public static long Long(this JsonElement e, string name) => e.GetProperty(name).GetInt64();
    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;
    public static bool Has(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public static List<JsonElement> Arr(this JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().ToList();

    public static async Task<string> DetailAsync(TestApp app, HttpResponseMessage res) =>
        (await app.Json(res)).GetProperty("detail").GetString()!;

    /// One SBMF frame: 20 ms of 16 kHz mono s16le (320 samples) with a rising sawtooth so a test can
    /// compare the exact samples that land in the stored WAV.
    public static byte[] MicFrame(uint seq, ulong captureTsMs, int samples = 320, byte flags = 0)
    {
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i % 256 - 128));
        return new MediaFrame(seq, captureTsMs, flags, pcm).ToBytes();
    }

    public static MediaFrame ParseFrame(byte[] bytes) =>
        MediaFrame.TryParse(bytes) ?? throw new InvalidOperationException("server sent an invalid SBMF frame");

    // ---------------- WAV helpers (read back what the server stored) ----------------

    public static (int SampleRate, int Channels, int Bits, int DataBytes) ReadWavHeader(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(bytes, 12, 4));
        int rate = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
        int channels = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22));
        int bits = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34));
        Assert.Equal("data", Encoding.ASCII.GetString(bytes, 36, 4));
        int dataLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
        Assert.Equal(bytes.Length - 44, dataLen);           // finalized header matches the file
        return (rate, channels, bits, dataLen);
    }

    /// Build a mono 16-bit WAV in memory (for the recorded-upload tests).
    public static byte[] BuildWav(int sampleRate, int samples, int channels = 1, int bits = 16, short value = 1000)
    {
        int dataBytes = samples * channels * bits / 8;
        var buf = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
        W32(buf, 4, 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(buf, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(buf, 12);
        W32(buf, 16, 16);
        W16(buf, 20, 1);                                     // PCM
        W16(buf, 22, channels);
        W32(buf, 24, sampleRate);
        W32(buf, 28, sampleRate * channels * bits / 8);
        W16(buf, 32, channels * bits / 8);
        W16(buf, 34, bits);
        Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
        W32(buf, 40, dataBytes);
        for (int i = 0; i < samples; i++)
            W16(buf, 44 + i * 2, value);
        return buf;
    }

    private static void W32(byte[] b, int off, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off), v);
    private static void W16(byte[] b, int off, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(off), (short)v);
}

public class LiveTranscriptWavTests
{
    [Fact]
    public void In_memory_live_snapshot_is_a_complete_mono_16k_wav()
    {
        var pcm = new byte[640];
        var wav = Wav.Mono16Bytes(pcm);
        var parsed = Wav.Parse(wav);
        Assert.NotNull(parsed);
        Assert.Equal(16_000, parsed!.Inspect.SampleRate);
        Assert.Equal(1, parsed.Inspect.Channels);
        Assert.Equal(16, parsed.Inspect.BitsPerSample);
        Assert.Equal(pcm.Length, parsed.Inspect.DataBytes);
        Assert.Equal(pcm, parsed.Pcm(wav).ToArray());
    }
}

/// Control WS client: handshake completed, then a background reader feeds NextAsync so a test can
/// wait for (or explicitly not-wait-for) a message without wedging.
public sealed class ControlClient : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly System.Threading.Channels.Channel<ControlEnvelope> _in =
        System.Threading.Channels.Channel.CreateUnbounded<ControlEnvelope>();
    private readonly Task _reader;

    public FakeDevice Device { get; }
    public WebSocket Socket => _ws;

    private ControlClient(FakeDevice device, WebSocket ws)
    {
        Device = device; _ws = ws;
        _reader = Task.Run(ReadLoop);
    }

    public static async Task<ControlClient> ConnectAsync(TestApp app, FakeDevice device, List<string>? capabilities = null)
    {
        var ws = await app.Factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/control"), CancellationToken.None);
        var challenge = await ReceiveAsync(ws);
        var nonce = challenge!.Payload!["nonce"]!.GetValue<string>();
        await SendAsync(ws, ControlEnvelope.Create(Protocol.TAuthResponse,
            PayloadSerializer.Node(new AuthResponsePayload(device.Id, "s2-test-client",
                AuthProof.Compute(device.Secret, nonce, device.Id), Capabilities: capabilities))));
        var ok = await ReceiveAsync(ws);
        Assert.Equal(Protocol.TAuthOk, ok!.Type);
        return new ControlClient(device, ws);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                var env = await ReceiveAsync(_ws);
                if (env is null) break;
                _in.Writer.TryWrite(env);
            }
        }
        catch (WebSocketException) { }
        finally { _in.Writer.TryComplete(); }
    }

    public static ControlEnvelope? EnvelopeOrNull(ControlEnvelope? e) => e;

    public async Task<ControlEnvelope?> NextAsync(int timeoutMs = 6000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await _in.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (System.Threading.Channels.ChannelClosedException) { return null; }
    }

    /// Read everything that is already queued (no waiting beyond a short settle).
    public async Task<List<ControlEnvelope>> DrainAsync(int settleMs = 600)
    {
        var list = new List<ControlEnvelope>();
        var deadline = DateTime.UtcNow.AddMilliseconds(settleMs);
        while (DateTime.UtcNow < deadline)
        {
            var e = await NextAsync(200);
            if (e is null) break;
            list.Add(e);
        }
        return list;
    }

    public Task SendAsync(ControlEnvelope env) => SendAsync(_ws, env);

    public static async Task SendAsync(WebSocket ws, ControlEnvelope env) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(env.ToJson()), WebSocketMessageType.Text, true, CancellationToken.None);

    public static async Task<ControlEnvelope?> ReceiveAsync(WebSocket ws)
    {
        var buffer = new byte[32 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            if (res.EndOfMessage) break;
        }
        return ControlEnvelope.Parse(sb.ToString());
    }

    public void Close() => _ws.Dispose();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }   // TestHost socket already torn down by the server side
        _ws.Dispose();
        try { await _reader; } catch { }
    }
}

/// Office mic client (/ws/mic): browser-like — sends an Origin header and the session cookie,
/// then binary SBMF frames.
public sealed class MicClient : IAsyncDisposable
{
    private readonly WebSocket _ws;
    public WebSocket Socket => _ws;

    private MicClient(WebSocket ws) => _ws = ws;

    public static async Task<MicClient> ConnectAsync(TestApp app, string leaseId, string origin, LiveSession session)
    {
        var client = app.Factory.Server.CreateWebSocketClient();
        // TestHost's ConfigureRequest gets the server-side HttpRequest for the upgrade, so the
        // browser-style Origin and Cookie headers are set exactly as a browser would send them.
        client.ConfigureRequest = request =>
        {
            request.Headers["Origin"] = origin;
            request.Headers["Cookie"] = session.CookieHeader;
        };
        var ws = await client.ConnectAsync(new Uri($"ws://localhost/ws/mic?lease_id={Uri.EscapeDataString(leaseId)}"),
            CancellationToken.None);
        return new MicClient(ws);
    }

    public Task SendFrameAsync(byte[] frameBytes) =>
        _ws.SendAsync(frameBytes, WebSocketMessageType.Binary, true, CancellationToken.None);

    public async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string reason = "operator stopped")
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException) { }
    }

    public void AbruptClose() => _ws.Dispose();     // simulates a tab close / lost socket

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _ws.Dispose();
    }
}

/// Device media client (/ws/media): redeems the OTP and collects SBMF frames in the background.
public sealed class MediaClient : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly System.Threading.Channels.Channel<byte[]> _frames =
        System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private readonly Task _reader;
    public WebSocket Socket => _ws;

    private MediaClient(WebSocket ws)
    {
        _ws = ws;
        _reader = Task.Run(ReadLoop);
    }

    public static async Task<(MediaClient? Client, int Status, string? Detail)> ConnectAsync(
        TestApp app, string announcementId, string deviceId, string token)
    {
        var ws = app.Factory.Server.CreateWebSocketClient();
        try
        {
            var sock = await ws.ConnectAsync(new Uri(
                $"ws://localhost/ws/media?announcement_id={Uri.EscapeDataString(announcementId)}" +
                $"&device_id={Uri.EscapeDataString(deviceId)}&token={Uri.EscapeDataString(token)}"), CancellationToken.None);
            if (sock.State != WebSocketState.Open)
            {
                var status = sock.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                return (null, (int)status, sock.CloseStatusDescription);
            }
            return (new MediaClient(sock), 101, null);
        }
        catch (WebSocketException e)
        {
            // TestHost reports a rejected upgrade as a failed connect
            return (null, 0, e.Message);
        }
    }

    private async Task ReadLoop()
    {
        var buffer = new byte[2048];
        try
        {
            while (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                var res = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.MessageType == WebSocketMessageType.Binary)
                    _frames.Writer.TryWrite(buffer.AsSpan(0, res.Count).ToArray());
            }
        }
        catch (WebSocketException) { }
        finally { _frames.Writer.TryComplete(); }
    }

    public async Task<byte[]?> NextFrameAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await _frames.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (System.Threading.Channels.ChannelClosedException) { return null; }
    }

    public async Task<List<MediaFrame>> DrainFramesAsync(int settleMs = 1500)
    {
        var list = new List<MediaFrame>();
        var deadline = DateTime.UtcNow.AddMilliseconds(settleMs);
        while (DateTime.UtcNow < deadline)
        {
            var raw = await NextFrameAsync(200);
            if (raw is null) break;
            list.Add(LiveTest.ParseFrame(raw));
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }   // TestHost socket already torn down by the server side
        _ws.Dispose();
        try { await _reader; } catch { }
    }
}

/// A second office browser session for the same TestApp (TestApp holds only one session at a
/// time, and WebSocket upgrades need the cookies on the upgrade request).
public sealed class LiveSession
{
    private readonly HttpClient _http;
    public string Csrf { get; private set; } = "";
    public string Session { get; private set; } = "";
    public string Username { get; }

    private LiveSession(HttpClient http, string username) { _http = http; Username = username; }

    public string CookieHeader => $"sb_csrf={Csrf}; sb_session={Session}";

    public static async Task<LiveSession> LoginAsync(TestApp app, string username, string password)
    {
        var http = app.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        var session = new LiveSession(http, username);

        var csrf = await http.GetAsync("/api/auth/csrf");
        Assert.Equal(System.Net.HttpStatusCode.OK, csrf.StatusCode);
        session.Csrf = Cookie(csrf, "sb_csrf");

        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/api/auth/login");
        req.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        req.Headers.TryAddWithoutValidation("X-CSRF", session.Csrf);
        req.Content = new System.Net.Http.StringContent(
            JsonSerializer.Serialize(new { username, password }), Encoding.UTF8, "application/json");
        var login = await http.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
        session.Session = Cookie(login, "sb_session");
        Assert.False(string.IsNullOrEmpty(session.Session));
        return session;
    }

    private static string Cookie(System.Net.Http.HttpResponseMessage res, string name)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var values)) return "";
        foreach (var raw in values)
        {
            var pair = raw.Split(';')[0];
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            if (pair[..idx] == name) return pair[(idx + 1)..];
        }
        return "";
    }
}

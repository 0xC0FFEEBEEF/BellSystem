// SchoolBell load simulator: N simulated endpoints speaking the real device protocol against a real
// server (enrolled credentials, HMAC auth, resync, status, ready/reject, single-use media tokens,
// receipts) plus a simulated office microphone. It measures TRANSPORT behaviour only — simulated
// devices do not render audio, so nothing here proves Windows playback (see TEST-RESULTS.md).
//
//   Simulator --server https://192.168.122.1:8443 --ca lab-ca.crt --admin labadmin --admin-pass-file pw
//             --devices 100 --live-s 20 --slow-pct 5 --server-pid 1234 --out results.json [--creds creds.json]
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SchoolBell.Contracts;

var opt = Args.Parse(args);
var sim = new Sim(opt);
return await sim.RunAsync();

sealed record Args(string Server, string? Ca, string Admin, string AdminPass, int Devices, int LiveS, int SlowPct,
    int? ServerPid, string Out, string Creds, int Parallel)
{
    public static Args Parse(string[] a)
    {
        string Get(string k, string? d = null)
        {
            var i = Array.IndexOf(a, k);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : d ?? throw new ArgumentException($"{k} is required");
        }
        return new Args(Get("--server").TrimEnd('/'), Get("--ca", ""), Get("--admin"), File.ReadAllText(Get("--admin-pass-file")).Trim(),
            int.Parse(Get("--devices", "100")), int.Parse(Get("--live-s", "20")), int.Parse(Get("--slow-pct", "0")),
            Get("--server-pid", "") is { Length: > 0 } p ? int.Parse(p) : null, Get("--out", "sim-results.json"),
            Get("--creds", "sim-creds.json"), int.Parse(Get("--parallel", "25")));
    }
}

sealed record Cred(string DeviceId, string Secret, string Name);

sealed class Sim
{
    private readonly Args _o;
    private readonly X509Certificate2? _ca;
    private readonly ConcurrentBag<double> _frameLatencyMs = new();
    private readonly ConcurrentDictionary<string, long> _ringArrivals = new();
    private readonly ConcurrentDictionary<string, long> _authAt = new();
    private long _bytesIn, _framesIn, _ready, _completedReceipts, _mediaConnects, _overflowCloses;
    private readonly JsonObject _results = new();
    private string? _ringId;
    private long _ringSentAt;

    public Sim(Args o)
    {
        _o = o;
        if (!string.IsNullOrEmpty(o.Ca)) _ca = X509CertificateLoader.LoadCertificateFromFile(o.Ca);
    }

    /// Proper chain validation against the lab CA (like curl --cacert): no certificate errors ignored.
    private bool Validate(object _, X509Certificate? cert, X509Chain? __, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (_ca is null || cert is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(cert));
    }

    private HttpClient Http(CookieContainer? jar = null) => new(new SocketsHttpHandler
    {
        CookieContainer = jar ?? new CookieContainer(), UseCookies = true,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
        MaxConnectionsPerServer = 64,
    }) { BaseAddress = new Uri(_o.Server), Timeout = TimeSpan.FromSeconds(30) };

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {m}");

    // ------------------------------------------------------------------ office session

    private CookieContainer _jar = new();
    private HttpClient? _office;
    private async Task<HttpClient> OfficeAsync()
    {
        if (_office is not null) return _office;
        _office = Http(_jar);
        await _office.GetAsync("/api/auth/csrf");
        await Post(_office, "/api/auth/login", new { username = _o.Admin, password = _o.AdminPass });
        return _office;
    }

    private async Task<JsonNode?> Post(HttpClient h, string path, object body)
    {
        var csrf = _jar.GetCookies(new Uri(_o.Server))["sb_csrf"]?.Value ?? "";
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF", csrf);
        req.Headers.Add("Origin", _o.Server);
        var resp = await h.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{path}: HTTP {(int)resp.StatusCode} {text}");
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    // ------------------------------------------------------------------ enrollment

    private async Task<List<Cred>> EnsureCredsAsync()
    {
        var creds = File.Exists(_o.Creds) ? JsonSerializer.Deserialize<List<Cred>>(File.ReadAllText(_o.Creds)) ?? [] : [];
        if (creds.Count >= _o.Devices) return creds.Take(_o.Devices).ToList();
        var office = await OfficeAsync();
        int need = _o.Devices - creds.Count;
        var tok = await Post(office, "/api/admin/enrollment-tokens", new { label = $"simulator {need}", ttl_hours = 2, max_uses = need });
        var token = tok?["token"]?.GetValue<string>() ?? throw new InvalidOperationException("no token returned");
        var pin = "";
        var sw = Stopwatch.StartNew();
        using var http = Http();
        var sem = new SemaphoreSlim(_o.Parallel);
        var added = new ConcurrentBag<Cred>();
        await Task.WhenAll(Enumerable.Range(creds.Count, need).Select(async i =>
        {
            await sem.WaitAsync();
            try
            {
                var name = $"sim-{i:D4}";
                var r = await http.PostAsJsonAsync("/api/enroll", new { token, device_name = name });
                r.EnsureSuccessStatusCode();
                var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
                pin = j["server_trust_pin"]!.GetValue<string>();
                added.Add(new Cred(j["device_id"]!.GetValue<string>(), j["device_secret"]!.GetValue<string>(), name));
            }
            finally { sem.Release(); }
        }));
        creds.AddRange(added.OrderBy(c => c.Name));
        File.WriteAllText(_o.Creds, JsonSerializer.Serialize(creds));
        Log($"enrolled {need} simulated devices in {sw.ElapsedMilliseconds} ms (credentials in {_o.Creds}, test-only)");
        return creds;
    }

    // ------------------------------------------------------------------ simulated device

    private sealed class Device
    {
        public required Cred Cred { get; init; }
        public required bool Slow { get; init; }
        public ClientWebSocket? Ws;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public volatile bool Connected;
        public string? Cursor;
        public CancellationTokenSource Life = new();
    }

    private async Task SendAsync(Device d, ControlEnvelope env)
    {
        var ws = d.Ws; if (ws is null) return;
        var bytes = Encoding.UTF8.GetBytes(env.ToJson());
        await d.SendLock.WaitAsync();
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception) { }
        finally { d.SendLock.Release(); }
    }

    private string WsUrl(string path) => (_o.Server.StartsWith("https") ? "wss" : "ws") + _o.Server[_o.Server.IndexOf(':')..] + path;

    private ClientWebSocket NewSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = Validate;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        return ws;
    }

    /// One control session; returns when the socket closes.
    private async Task SessionAsync(Device d)
    {
        var ws = NewSocket();
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ws.ConnectAsync(new Uri(WsUrl("/ws/control")), connectTimeout.Token);
        d.Ws = ws;
        var buf = new byte[64 * 1024];
        async Task<ControlEnvelope?> Recv()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, d.Life.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (r.EndOfMessage) return ControlEnvelope.Parse(sb.ToString());
            }
        }
        var challenge = await Recv() ?? throw new InvalidOperationException("no challenge");
        var nonce = PayloadSerializer.ToPayload<AuthChallengePayload>(challenge)!.Nonce;
        await SendAsync(d, ControlEnvelope.Create(Protocol.TAuthResponse, PayloadSerializer.Node(
            new AuthResponsePayload(d.Cred.DeviceId, "sim-1.0", AuthProof.Compute(d.Cred.Secret, nonce, d.Cred.DeviceId)))));
        var ok = await Recv();
        if (ok?.Type != Protocol.TAuthOk) throw new InvalidOperationException("auth failed");
        _authAt[d.Cred.DeviceId] = Now();
        d.Connected = true;
        await SendAsync(d, ControlEnvelope.Create(Protocol.TDeviceResync, PayloadSerializer.Node(new DeviceResyncPayload(d.Cursor, 0, []))));
        await SendAsync(d, Status(d));
        using var statusTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        _ = Task.Run(async () => { try { while (await statusTimer.WaitForNextTickAsync(d.Life.Token)) await SendAsync(d, Status(d)); } catch { } });
        try
        {
            while (true)
            {
                var env = await Recv();
                if (env is null) break;
                if (env.EventId is not null) d.Cursor = env.EventId;
                await HandleAsync(d, env);
            }
        }
        finally { d.Connected = false; }
    }

    private static ControlEnvelope Status(Device d) => ControlEnvelope.Create(Protocol.TDeviceStatus, PayloadSerializer.Node(
        new DeviceStatusPayload(d.Cred.Name, null, null, "ready", null, "Simulated speaker", true, false, 50, false, 100, 0, 0, 0,
            new KeepAwakeStatus(false, false, 0), null, "running")));

    private async Task HandleAsync(Device d, ControlEnvelope env)
    {
        switch (env.Type)
        {
            case Protocol.TBellRing:
                var ring = PayloadSerializer.ToPayload<BellRingPayload>(env)!;
                _ringArrivals[d.Cred.DeviceId] = Now();
                await SendAsync(d, ControlEnvelope.Create(Protocol.TReceipt, PayloadSerializer.Node(new ReceiptPayload(ring.RingId, ReceiptState.Completed, Now(), null))));
                Interlocked.Increment(ref _completedReceipts);
                break;
            case Protocol.TAnnouncementOffer:
                var offer = PayloadSerializer.ToPayload<AnnouncementOfferPayload>(env)!;
                await SendAsync(d, ControlEnvelope.Create(Protocol.TAnnouncementReady, PayloadSerializer.Node(new AnnouncementReadyPayload(offer.AnnouncementId, "Simulated speaker"))));
                Interlocked.Increment(ref _ready);
                break;
            case Protocol.TMediaToken:
                var t = PayloadSerializer.ToPayload<MediaTokenPayload>(env)!;
                _ = Task.Run(() => MediaAsync(d, t));
                break;
            case Protocol.TAnnouncementEnd:
                var end = PayloadSerializer.ToPayload<AnnouncementEndPayload>(env)!;
                await SendAsync(d, ControlEnvelope.Create(Protocol.TReceipt, PayloadSerializer.Node(new ReceiptPayload(end.AnnouncementId, ReceiptState.Completed, Now(), null))));
                break;
        }
    }

    private async Task MediaAsync(Device d, MediaTokenPayload t)
    {
        using var ws = NewSocket();
        try
        {
            await ws.ConnectAsync(new Uri(WsUrl($"/ws/media?announcement_id={t.AnnouncementId}&device_id={d.Cred.DeviceId}&token={t.Token}")), CancellationToken.None);
            Interlocked.Increment(ref _mediaConnects);
            var buf = new byte[2048];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.CloseStatusDescription?.Contains("overflow") == true) Interlocked.Increment(ref _overflowCloses);
                    break;
                }
                var f = MediaFrame.TryParse(buf.AsSpan(0, r.Count));
                if (f is null) continue;
                Interlocked.Add(ref _bytesIn, r.Count);
                Interlocked.Increment(ref _framesIn);
                _frameLatencyMs.Add(Now() - (double)f.Value.CaptureTsMs);
                if (d.Slow) await Task.Delay(60);   // reads 3x slower than real time: a slow consumer
                if (f.Value.IsLast) break;
            }
        }
        catch (Exception) { }
    }

    private async Task DeviceLoopAsync(Device d, CancellationToken stop)
    {
        var rnd = new Random(d.Cred.DeviceId.GetHashCode());
        int attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            try { await SessionAsync(d); attempt = 0; }
            catch (Exception) when (!stop.IsCancellationRequested) { }
            if (stop.IsCancellationRequested) break;
            // the real client's policy: immediate first retry, then full jitter up to a 30 s cap
            var capMs = Math.Min(30_000, 1000 * (1 << Math.Min(attempt, 5)));
            var delay = attempt == 0 ? 0 : rnd.Next(0, capMs);
            attempt++;
            try { await Task.Delay(delay, stop); } catch (OperationCanceledException) { break; }
            d.Life = new CancellationTokenSource();
        }
    }

    // ------------------------------------------------------------------ server resource sampling

    private readonly List<JsonObject> _samples = [];
    private async Task SampleAsync(string phase, CancellationToken ct)
    {
        if (_o.ServerPid is not int pid) return;
        long lastTicks = ProcTicks(pid); var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            long ticks = ProcTicks(pid);
            double cpu = (ticks - lastTicks) / 100.0 / sw.Elapsed.TotalSeconds * 100.0;   // % of one core
            lastTicks = ticks; sw.Restart();
            lock (_samples) _samples.Add(new JsonObject { ["phase"] = phase, ["t"] = Now(), ["cpu_pct_one_core"] = Math.Round(cpu, 1), ["rss_mb"] = RssMb(pid), ["threads"] = Threads(pid) });
        }
    }
    private static long ProcTicks(int pid) { var f = File.ReadAllText($"/proc/{pid}/stat").Split(')')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries); return long.Parse(f[11]) + long.Parse(f[12]); }
    private static double RssMb(int pid) => Math.Round(long.Parse(File.ReadAllLines($"/proc/{pid}/status").First(l => l.StartsWith("VmRSS:")).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024.0, 1);
    private static int Threads(int pid) => int.Parse(File.ReadAllLines($"/proc/{pid}/status").First(l => l.StartsWith("Threads:")).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1]);
    private JsonObject Summarize(string phase)
    {
        List<JsonObject> s; lock (_samples) s = _samples.Where(x => x["phase"]!.GetValue<string>() == phase).ToList();
        if (s.Count == 0) return new JsonObject { ["samples"] = 0 };
        var cpu = s.Select(x => x["cpu_pct_one_core"]!.GetValue<double>()).ToList();
        var rss = s.Select(x => x["rss_mb"]!.GetValue<double>()).ToList();
        return new JsonObject { ["samples"] = s.Count, ["cpu_avg_pct_one_core"] = Math.Round(cpu.Average(), 1), ["cpu_max_pct_one_core"] = cpu.Max(),
            ["rss_max_mb"] = rss.Max(), ["threads_max"] = s.Max(x => x["threads"]!.GetValue<int>()) };
    }

    private static JsonObject Pct(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return new JsonObject { ["n"] = 0 };
        double P(double p) => v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)];
        return new JsonObject { ["n"] = v.Count, ["p50"] = Math.Round(P(0.5), 1), ["p95"] = Math.Round(P(0.95), 1), ["p99"] = Math.Round(P(0.99), 1), ["max"] = Math.Round(v[^1], 1) };
    }

    // ------------------------------------------------------------------ run

    public async Task<int> RunAsync()
    {
        _results["devices"] = _o.Devices; _results["slow_pct"] = _o.SlowPct; _results["server"] = _o.Server;
        _results["host"] = Environment.MachineName; _results["cores"] = Environment.ProcessorCount; _results["started_at"] = DateTimeOffset.UtcNow.ToString("o");
        var creds = await EnsureCredsAsync();
        int slow = _o.Devices * _o.SlowPct / 100;
        var devices = creds.Select((c, i) => new Device { Cred = c, Slow = i < slow }).ToList();
        using var stop = new CancellationTokenSource();

        // 1. connect everybody
        var phase = new CancellationTokenSource(); var sampler = SampleAsync("connect", phase.Token);
        var t0 = Now();
        var loops = devices.Select(d => Task.Run(() => DeviceLoopAsync(d, stop.Token))).ToList();
        while (devices.Count(d => d.Connected) < devices.Count && Now() - t0 < 120_000) await Task.Delay(100);
        var connectMs = Now() - t0;
        _results["connect"] = new JsonObject { ["connected"] = devices.Count(d => d.Connected), ["all_connected_ms"] = connectMs,
            ["auth_spread"] = Pct(_authAt.Values.Select(v => (double)(v - t0))) };
        Log($"connected {devices.Count(d => d.Connected)}/{devices.Count} in {connectMs} ms");
        await Task.Delay(5000);
        phase.Cancel(); await sampler; _results["connect"]!["server"] = Summarize("connect");

        // 2. idle steady state
        phase = new CancellationTokenSource(); sampler = SampleAsync("idle", phase.Token);
        await Task.Delay(15_000);
        phase.Cancel(); await sampler; _results["idle"] = new JsonObject { ["server"] = Summarize("idle"), ["connected"] = devices.Count(d => d.Connected) };

        // 3. ring now to all: delivery spread + receipt lag
        var office = await OfficeAsync();
        phase = new CancellationTokenSource(); sampler = SampleAsync("ring", phase.Token);
        _ringArrivals.Clear();
        _ringSentAt = Now();
        var ring = await Post(office, "/api/bells/ring", new { target = new { all = true }, bell_name = "simulator ring" });
        _ringId = ring?["ring_id"]?.GetValue<string>();
        while (_ringArrivals.Count < devices.Count && Now() - _ringSentAt < 30_000) await Task.Delay(50);
        await Task.Delay(3000);
        var ringsResp = JsonNode.Parse(await office.GetStringAsync("/api/bells/rings?limit=1"))!.AsArray()[0]!;
        phase.Cancel(); await sampler;
        _results["ring"] = new JsonObject { ["delivered"] = _ringArrivals.Count, ["arrival_after_request_ms"] = Pct(_ringArrivals.Values.Select(v => (double)(v - _ringSentAt))),
            ["server_counts"] = ringsResp["counts"]!.DeepClone(), ["server"] = Summarize("ring") };
        Log($"ring delivered to {_ringArrivals.Count}/{devices.Count}");

        // 4. live announcement fan-out with a simulated office microphone (real-time 20 ms frames)
        phase = new CancellationTokenSource(); sampler = SampleAsync("live", phase.Token);
        _frameLatencyMs.Clear(); Interlocked.Exchange(ref _bytesIn, 0); Interlocked.Exchange(ref _framesIn, 0); Interlocked.Exchange(ref _ready, 0);
        var lease = await Post(office, "/api/announcements/lease", new { target = new { all = true }, max_duration_s = Math.Max(60, _o.LiveS + 30) });
        var leaseId = lease!["lease_id"]!.GetValue<string>();
        var live = await Post(office, "/api/announcements/live", new { target = new { all = true }, chime = false, lease_id = leaseId });
        var annId = live!["announcement_id"]!.GetValue<string>();
        var windowClose = live["window_closes_at"]?.GetValue<long>() ?? Now() + 5000;
        using var mic = NewSocket();
        mic.Options.Cookies = _jar;
        mic.Options.SetRequestHeader("Origin", _o.Server);
        await mic.ConnectAsync(new Uri(WsUrl($"/ws/mic?lease_id={leaseId}")), CancellationToken.None);
        await Task.Delay((int)Math.Max(0, windowClose - Now() + 300));
        var sendStart = Now(); uint seq = 0; var pcm = new byte[640];
        var clock = Stopwatch.StartNew();
        int frames = _o.LiveS * 50;
        for (int i = 0; i < frames; i++)
        {
            for (int s = 0; s < 320; s++) { short v = (short)(Math.Sin((i * 320 + s) * 2 * Math.PI * 440 / 16000) * 6000); pcm[2 * s] = (byte)v; pcm[2 * s + 1] = (byte)(v >> 8); }
            var f = new MediaFrame(++seq, (ulong)Now(), i == frames - 1 ? MediaFrame.FlagLast : (byte)0, pcm.ToArray());
            await mic.SendAsync(f.ToBytes(), WebSocketMessageType.Binary, true, CancellationToken.None);
            var due = (i + 1) * 20 - clock.ElapsedMilliseconds;
            if (due > 1) await Task.Delay((int)due);
        }
        var sendMs = Now() - sendStart;
        await Task.Delay(3000);
        await Post(office, $"/api/announcements/live/end?announcement_id={annId}", new { });
        try { await mic.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await Task.Delay(3000);
        phase.Cancel(); await sampler;
        double egressMbps = Interlocked.Read(ref _bytesIn) * 8.0 / 1e6 / (sendMs / 1000.0);
        var fast = _frameLatencyMs.ToList();
        _results["live"] = new JsonObject
        {
            ["ready_devices"] = Interlocked.Read(ref _ready), ["media_connects"] = Interlocked.Read(ref _mediaConnects),
            ["frames_sent_by_mic"] = frames, ["frames_received_total"] = Interlocked.Read(ref _framesIn),
            ["expected_frames_total"] = frames * devices.Count, ["slow_consumer_overflow_closes"] = Interlocked.Read(ref _overflowCloses),
            ["media_egress_mbps_received"] = Math.Round(egressMbps, 1),
            ["frame_latency_capture_to_device_ms"] = Pct(fast),
            ["server"] = Summarize("live"),
            ["note"] = "latency = simulated-device receive time minus office capture timestamp (same host clock); transport only, excludes the renderer jitter buffer and audio output",
        };
        Log($"live: {Interlocked.Read(ref _framesIn)} frames received, egress {egressMbps:F1} Mbps, p95 {_results["live"]!["frame_latency_capture_to_device_ms"]!["p95"]} ms");

        // 5. reconnect storm: every control socket drops at once
        phase = new CancellationTokenSource(); sampler = SampleAsync("reconnect", phase.Token);
        _authAt.Clear();
        var drop = Now();
        foreach (var d in devices) { d.Life.Cancel(); try { d.Ws?.Abort(); } catch { } }
        await Task.Delay(500);
        while (devices.Count(d => d.Connected) < devices.Count && Now() - drop < 120_000) await Task.Delay(100);
        _results["reconnect_storm"] = new JsonObject { ["reconnected"] = devices.Count(d => d.Connected), ["all_reconnected_ms"] = Now() - drop,
            ["reconnect_after_drop_ms"] = Pct(_authAt.Values.Select(v => (double)(v - drop))), ["server"] = Summarize("reconnect") };
        phase.Cancel(); await sampler;
        Log($"reconnect storm: {devices.Count(d => d.Connected)}/{devices.Count} back in {Now() - drop} ms");

        try
        {
            var diag = JsonNode.Parse(await office.GetStringAsync("/api/diagnostics"));
            _results["server_diagnostics_after"] = diag;
        }
        catch { }
        stop.Cancel();
        foreach (var d in devices) { d.Life.Cancel(); try { d.Ws?.Abort(); } catch { } }
        _results["finished_at"] = DateTimeOffset.UtcNow.ToString("o");
        File.WriteAllText(_o.Out, _results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log($"results written to {_o.Out}");
        return 0;
    }
}

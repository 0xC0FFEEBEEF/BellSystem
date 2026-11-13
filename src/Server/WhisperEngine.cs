using System.Net.Http.Headers;
using System.Text.Json;

namespace SchoolBell.Server;

/// Local whisper.cpp inference server on loopback (schoolbell-whisper.service, model small.en,
/// capped threads/CPU). Transcription is queued and never blocks playback; a stopped or crashed
/// whisper service leaves jobs queued/retrying and the transcript "pending" or "failed".
public sealed class WhisperCppEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim _inference = new(1, 1);
    private int _finalWaiting;
    private readonly IHttpClientFactory _http;
    private readonly ServerConfig _cfg;
    private readonly ILogger<WhisperCppEngine> _log;

    public WhisperCppEngine(IHttpClientFactory http, ServerConfig cfg, ILogger<WhisperCppEngine> log)
    {
        _http = http; _cfg = cfg; _log = log;
    }

    public string Name => "whisper.cpp";
    public bool Available => true;

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await _http.CreateClient("whisper").GetAsync(_cfg.WhisperUrl.TrimEnd('/') + "/health", cts.Token);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "ok";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return false; }
    }

    public async Task<string?> TranscribeAsync(ReadOnlyMemory<byte> wav, CancellationToken ct)
    {
        Interlocked.Increment(ref _finalWaiting);
        try { await _inference.WaitAsync(ct); }
        finally { Interlocked.Decrement(ref _finalWaiting); }
        try { return await InferAsync(wav, ct); }
        finally { _inference.Release(); }
    }

    public async Task<string?> TranscribePreviewAsync(ReadOnlyMemory<byte> wav, CancellationToken ct)
    {
        if (Volatile.Read(ref _finalWaiting) > 0 || !await _inference.WaitAsync(0, ct)) return null;
        try { return await InferAsync(wav, ct); }
        finally { _inference.Release(); }
    }

    private async Task<string?> InferAsync(ReadOnlyMemory<byte> wav, CancellationToken ct)
    {
        var bytes = wav.ToArray();
        var parsed = Wav.Parse(bytes);
        if (parsed is null || parsed.Inspect is not { Format: 1, Channels: 1, BitsPerSample: 16 })
            throw new InvalidDataException("transcription requires a valid mono 16-bit PCM recording");
        // Only digital zero is certainly silence. Quiet speech must be judged by Whisper.
        var pcm = parsed.Pcm(bytes);
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm[i..])));
        if (peak == 0)
        {
            _log.LogInformation("recording is digitally silent; skipping Whisper inference");
            return "[no speech detected]";
        }
        // Normalize the inference copy only. Speaker playback and the stored recording stay exact.
        // A bounded gain helps low microphone levels without aggressively boosting background noise.
        double gain = peak < 4096 ? Math.Min(32.0, 8192.0 / peak) : 1.0;
        if (gain > 1)
        {
            var samples = bytes.AsSpan(parsed.DataOffset, (int)parsed.Inspect.DataBytes);
            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                int sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(samples[i..]);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples[i..], (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue));
            }
            _log.LogInformation("amplified quiet transcription input by {Gain:F1}x (original peak {Peak})", gain, peak);
        }
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "announcement.wav");
        form.Add(new StringContent("en"), "language");
        form.Add(new StringContent("0.0"), "temperature");
        form.Add(new StringContent("0.2"), "temperature_inc");
        form.Add(new StringContent("json"), "response_format");
        using var resp = await _http.CreateClient("whisper").PostAsync(_cfg.WhisperUrl.TrimEnd('/') + "/inference", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"whisper server HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err)) throw new InvalidOperationException($"whisper error: {err}");
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
        return text?.Trim();
    }

}

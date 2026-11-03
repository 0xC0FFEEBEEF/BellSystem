using System.Net.Http.Headers;
using System.Text.Json;

namespace SchoolBell.Server;

/// Local whisper.cpp inference server on loopback (schoolbell-whisper.service, model small.en,
/// capped threads/CPU). Transcription is queued and never blocks playback; a stopped or crashed
/// whisper service leaves jobs queued/retrying and the transcript "pending" or "failed".
public sealed class WhisperCppEngine : ITranscriptionEngine
{
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
            using var resp = await _http.CreateClient("whisper").GetAsync(_cfg.WhisperUrl.TrimEnd('/') + "/", cts.Token);
            return (int)resp.StatusCode < 500;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    public async Task<string?> TranscribeAsync(string wavPath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(wavPath, ct);
        if (IsSilent(bytes))
        {
            _log.LogInformation("recording {Path} is silent; skipping inference", Path.GetFileName(wavPath));
            return "[no speech detected]";
        }
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "announcement.wav");
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

    /// Peak below -50 dBFS over a 16-bit mono WAV body = silence.
    private static bool IsSilent(byte[] wav)
    {
        if (wav.Length <= 44) return true;
        int peak = 0;
        for (int i = 44; i + 1 < wav.Length; i += 2)
        {
            int v = Math.Abs((short)(wav[i] | (wav[i + 1] << 8)));
            if (v > peak) { peak = v; if (peak > 104) return false; } // 104/32768 = -50 dBFS
        }
        return true;
    }
}

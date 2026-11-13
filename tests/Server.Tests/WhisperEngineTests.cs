using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SchoolBell.Server;

namespace Server.Tests;

public class WhisperEngineTests
{
    sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };
    static WhisperCppEngine Engine(IHttpClientFactory factory) => new(factory, new ServerConfig(), NullLogger<WhisperCppEngine>.Instance);

    [Theory]
    [InlineData(16)] // quiet, nonzero speech used to be falsely discarded as silence
    [InlineData(-32768)] // clipped PCM used to overflow Math.Abs(short.MinValue)
    public async Task Nonzero_audio_always_reaches_whisper(int sample)
    {
        bool called = false;
        var wav = LiveTest.BuildWav(16000, 16000, value: (short)sample);
        var original = wav.ToArray();
        using var factory = new Factory(async (request, ct) =>
        {
            Assert.EndsWith("/inference", request.RequestUri!.AbsolutePath);
            called = true;
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var audio = await form.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "file").ReadAsByteArrayAsync(ct);
            var parsed = Wav.Parse(audio)!;
            int sentSample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(parsed.Pcm(audio));
            if (sample == 16) Assert.True(sentSample > sample); // quiet recognition input is amplified
            else Assert.Equal(sample, sentSample); // loud/clipped input is never increased
            return Json("{\"text\":\"Good morning students.\"}");
        });
        var text = await Engine(factory).TranscribeAsync(wav, default);
        Assert.True(called);
        Assert.Equal("Good morning students.", text);
        Assert.Equal(original, wav);
    }

    [Fact]
    public async Task Model_health_requires_ready_status_not_just_an_http_page()
    {
        using var factory = new Factory((r, ct) =>
        {
            Assert.EndsWith("/health", r.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"status\":\"loading model\"}", HttpStatusCode.ServiceUnavailable));
        });
        Assert.False(await Engine(factory).IsAvailableAsync(default));
    }

    [Fact]
    public async Task Preview_requests_do_not_queue_behind_final_transcription()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var factory = new Factory(async (r, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return Json("{\"text\":\"Speech text\"}");
        });
        var engine = Engine(factory);
        var wav = LiveTest.BuildWav(16000, 16000, value: 1000);
        var preview = engine.TranscribePreviewAsync(wav, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var final = engine.TranscribeAsync(wav, default);
        Assert.Null(await engine.TranscribePreviewAsync(wav, default));
        release.TrySetResult();
        await preview;
        Assert.Equal("Speech text", await final);
        Assert.Equal(2, calls);
    }
}

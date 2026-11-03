using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SchoolBell.Client.Core;

namespace SchoolBell.Client.Audio;

/// Mono float samples in memory (bells, chime) with an optional fade-out tail.
internal sealed class MonoBufferSource : ISampleProvider
{
    private readonly float[] _samples;
    private int _pos;
    public WaveFormat WaveFormat { get; }

    public MonoBufferSource(float[] samples, int sampleRate)
    {
        _samples = samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = Math.Min(count, _samples.Length - _pos);
        Array.Copy(_samples, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }
}

/// Pulls 16 kHz mono from the renderer jitter buffer, after an optional lead-in of silence
/// (so a chime finishes before speech starts).
internal sealed class JitterSource : ISampleProvider
{
    private readonly PcmJitterBuffer _jitter;
    private long _leadInSamples;
    public WaveFormat WaveFormat { get; }

    public JitterSource(PcmJitterBuffer jitter, long leadInSamples)
    {
        _jitter = jitter;
        _leadInSamples = leadInSamples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(jitter.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_leadInSamples > 0)
        {
            int n = (int)Math.Min(count, _leadInSamples);
            Array.Clear(buffer, offset, n);
            _leadInSamples -= n;
            return n == count ? n : n + _jitter.Read(buffer.AsSpan(offset + n, count - n));
        }
        return _jitter.Read(buffer.AsSpan(offset, count));
    }
}

/// Mono -> N output channels (front left/right carry the signal; other channels stay silent).
internal sealed class MonoToChannels : ISampleProvider
{
    private readonly ISampleProvider _src;
    private float[] _tmp = [];
    public WaveFormat WaveFormat { get; }

    public MonoToChannels(ISampleProvider mono, int channels)
    {
        _src = mono;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mono.WaveFormat.SampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = WaveFormat.Channels;
        int frames = count / ch;
        if (_tmp.Length < frames) _tmp = new float[frames];
        int n = _src.Read(_tmp, 0, frames);
        for (int i = 0; i < n; i++)
        {
            int b = offset + i * ch;
            buffer[b] = _tmp[i];
            if (ch > 1) buffer[b + 1] = _tmp[i];
            for (int c = 2; c < ch; c++) buffer[b + c] = 0f;
        }
        return n * ch;
    }
}

/// Signals when the wrapped source ends (mixer removes it) or is cancelled.
internal sealed class TrackedSource : ISampleProvider
{
    private readonly ISampleProvider _inner;
    private volatile bool _cancelled;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WaveFormat WaveFormat => _inner.WaveFormat;
    public Task Completion => _done.Task;
    public long SamplesRead { get; private set; }
    public bool Cancelled => _cancelled;

    public TrackedSource(ISampleProvider inner) => _inner = inner;

    public void Cancel() => _cancelled = true;

    /// Called by the engine when the output is torn down while this source is still queued.
    public void Abandon() => _done.TrySetResult();

    public int Read(float[] buffer, int offset, int count)
    {
        if (_cancelled) { _done.TrySetResult(); return 0; }
        int n = _inner.Read(buffer, offset, count);
        SamplesRead += n;
        if (n < count) _done.TrySetResult();
        return n;
    }
}

internal static class SoundLoader
{
    /// Decode a WAV (PCM 8/16/24/32 or float, any channel count) to mono float at `rate`,
    /// truncated to `maxSeconds` (>0) with a fade-out of `fadeMs` when truncated.
    public static float[] LoadMono(string path, int rate, int maxSeconds, int fadeMs)
    {
        using var reader = new WaveFileReader(path);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1) sp = new DownmixToMono(sp);
        if (sp.WaveFormat.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);
        var list = new List<float>(rate * 4);
        var buf = new float[rate / 10];
        long cap = maxSeconds > 0 ? (long)maxSeconds * rate : long.MaxValue;
        int n;
        while (list.Count < cap && (n = sp.Read(buf, 0, buf.Length)) > 0)
            list.AddRange(buf.AsSpan(0, (int)Math.Min(n, cap - list.Count)).ToArray());
        var samples = list.ToArray();
        bool truncated = maxSeconds > 0 && samples.Length >= cap;
        if (truncated) ApplyFadeOut(samples, rate * Math.Clamp(fadeMs, 0, 2000) / 1000);
        return samples;
    }

    public static void ApplyFadeOut(float[] s, int fadeSamples)
    {
        fadeSamples = Math.Min(fadeSamples, s.Length);
        for (int i = 0; i < fadeSamples; i++)
            s[s.Length - fadeSamples + i] *= 1f - (float)i / fadeSamples;
    }

    /// Two-tone office chime (659 Hz then 523 Hz, decaying), about 0.9 s.
    public static float[] Chime(int rate)
    {
        int tone = rate * 45 / 100;
        var s = new float[tone * 2];
        for (int i = 0; i < s.Length; i++)
        {
            double f = i < tone ? 659.25 : 523.25;
            double t = (i % tone) / (double)rate;
            double env = Math.Exp(-4.0 * t) * Math.Min(1.0, t * 200);
            s[i] = (float)(0.35 * env * (Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(4 * Math.PI * f * t)));
        }
        return s;
    }
}

internal sealed class DownmixToMono : ISampleProvider
{
    private readonly ISampleProvider _src;
    private float[] _tmp = [];
    public WaveFormat WaveFormat { get; }
    public DownmixToMono(ISampleProvider src)
    {
        _src = src;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1);
    }
    public int Read(float[] buffer, int offset, int count)
    {
        int ch = _src.WaveFormat.Channels;
        if (_tmp.Length < count * ch) _tmp = new float[count * ch];
        int n = _src.Read(_tmp, 0, count * ch) / ch;
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += _tmp[i * ch + c];
            buffer[offset + i] = sum / ch;
        }
        return n;
    }
}

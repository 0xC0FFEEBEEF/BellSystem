namespace SchoolBell.Client.Core;

/// Renderer-side jitter buffer for live/recorded announcement audio (PROTOCOL §7), paced by the
/// audio device clock: the output thread pulls samples with Read(). It waits for `startMs` of
/// audio before playing, plays silence on underrun and re-buffers, and never holds more than
/// `maxMs` (the oldest audio is discarded instead of playing a delayed backlog). Missing frames
/// are not invented. After End(), the remaining audio drains and Read() then returns 0.
public sealed class PcmJitterBuffer
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _startSamples;
    private int _head, _count;
    private bool _priming = true, _ended;

    public int SampleRate { get; }
    public long Underruns { get; private set; }
    public long DroppedSamples { get; private set; }
    public long SamplesWritten { get; private set; }
    public long SamplesPlayed { get; private set; }

    public PcmJitterBuffer(int sampleRate = 16_000, int startMs = 300, int maxMs = 2_000)
    {
        SampleRate = sampleRate;
        _startSamples = Math.Max(1, sampleRate * Math.Clamp(startMs, 20, 2_000) / 1000);
        int maxSamples = Math.Max(_startSamples * 2, sampleRate * Math.Clamp(maxMs, startMs, 10_000) / 1000);
        _ring = new float[maxSamples];
    }

    public int BufferedMs { get { lock (_gate) return (int)(_count * 1000L / SampleRate); } }
    public bool Ended { get { lock (_gate) return _ended; } }
    public bool Finished { get { lock (_gate) return _ended && _count == 0; } }
    public bool Priming { get { lock (_gate) return _priming; } }

    /// Append PCM s16le mono samples.
    public void Write(ReadOnlySpan<byte> pcm16)
    {
        int n = pcm16.Length / 2;
        lock (_gate)
        {
            if (_ended) return;
            for (int i = 0; i < n; i++)
            {
                short v = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
                if (_count == _ring.Length)
                {
                    _head = (_head + 1) % _ring.Length;   // drop oldest: bounded latency
                    _count--;
                    DroppedSamples++;
                }
                _ring[(_head + _count) % _ring.Length] = v / 32768f;
                _count++;
            }
            SamplesWritten += n;
        }
    }

    public void End() { lock (_gate) _ended = true; }

    /// Fill `dest`. Returns dest.Length while the stream is open (silence while buffering);
    /// once ended, returns the number of real samples left (0 = finished).
    public int Read(Span<float> dest)
    {
        lock (_gate)
        {
            if (_priming && !_ended)
            {
                if (_count < _startSamples) { dest.Clear(); return dest.Length; }
                _priming = false;
            }
            int take = Math.Min(_count, dest.Length);
            for (int i = 0; i < take; i++)
            {
                dest[i] = _ring[_head];
                _head = (_head + 1) % _ring.Length;
            }
            _count -= take;
            SamplesPlayed += take;
            if (take == dest.Length) return take;
            if (_ended) return take;
            // underrun while still live: pad with silence and re-buffer before resuming
            dest[take..].Clear();
            if (SamplesWritten > 0) Underruns++;
            _priming = true;
            return dest.Length;
        }
    }
}

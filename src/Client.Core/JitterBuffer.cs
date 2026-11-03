using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Client jitter buffer (PROTOCOL §7): start delay 200–400 ms (default 300), hard max
/// buffered audio 2000 ms. Frames at or below the last emitted sequence are stale and
/// dropped; sequence jumps are counted as gaps (missing frames are never invented);
/// on last-frame or explicit end, remaining valid audio drains.
public sealed class JitterBuffer
{
    public const int FrameAudioMs = Protocol.FrameMs;

    private readonly object _gate = new();
    private readonly LinkedList<MediaFrame> _frames = new();
    private readonly int _startMs;
    private readonly int _maxMs;
    private long _lastEmittedSeq = -1;
    private long _gapsDropped;
    private long _staleDropped;
    private long _overflowDropped;
    private bool _ended;

    public JitterBuffer(int startMs = Protocol.JitterBufferStartMs, int maxMs = Protocol.JitterBufferMaxMs)
    {
        _startMs = Math.Clamp(startMs, 200, 400);
        _maxMs = Math.Clamp(maxMs, _startMs, 10_000);
    }

    public long GapsDropped { get { lock (_gate) return _gapsDropped; } }
    public long StaleDropped { get { lock (_gate) return _staleDropped; } }
    public long OverflowDropped { get { lock (_gate) return _overflowDropped; } }
    public int BufferedFrames { get { lock (_gate) return _frames.Count; } }
    public int BufferedMs => BufferedFrames * FrameAudioMs;
    public int StartMs => _startMs;
    public int MaxMs => _maxMs;

    public void Push(MediaFrame frame)
    {
        lock (_gate)
        {
            if (_ended) { _staleDropped++; return; }
            long highSeq = _frames.Count == 0 ? _lastEmittedSeq : _frames.Last!.Value.Seq;
            if (frame.Seq <= highSeq)
            {
                _staleDropped++; // duplicate or out-of-order frame: obsolete, drop
                return;
            }
            if (frame.Seq > highSeq + 1)
                _gapsDropped += frame.Seq - highSeq - 1; // count missing frames; do not fabricate
            _frames.AddLast(frame);
            TrimToMax();
        }
    }

    /// Frames are only emitted after the start delay has been buffered
    /// (arrival pacing is the caller's job; this tracks audio duration held).
    public MediaFrame? Dequeue()
    {
        lock (_gate)
        {
            if (_frames.Count == 0) return null;
            if (!_ended && _frames.Count * FrameAudioMs < _startMs) return null;
            var f = _frames.First!.Value;
            _frames.RemoveFirst();
            _lastEmittedSeq = Math.Max(_lastEmittedSeq, f.Seq);
            return f;
        }
    }

    /// All remaining buffered frames (on flags.last or announcement end): drain valid audio.
    public List<MediaFrame> Drain()
    {
        lock (_gate)
        {
            _ended = true;
            var list = _frames.ToList();
            _frames.Clear();
            if (list.Count > 0) _lastEmittedSeq = Math.Max(_lastEmittedSeq, list[^1].Seq);
            return list;
        }
    }

    /// After a pass-through Drain(): accept further frames (sequence state is kept, so
    /// duplicates and stale frames are still rejected).
    public void Reopen()
    {
        lock (_gate) _ended = false;
    }

    private void TrimToMax()
    {
        // Hard bound: drop oldest buffered audio beyond the max (this is "discard obsolete
        // audio instead of playing a delayed backlog").
        while (_frames.Count * FrameAudioMs > _maxMs)
        {
            _frames.RemoveFirst();
            _overflowDropped++;
        }
    }
}

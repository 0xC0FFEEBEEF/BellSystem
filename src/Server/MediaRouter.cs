using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Net.WebSockets;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record QueueDepth(string AnnouncementId, int Devices, int MaxFrames, double MeanFrames);

/// Per-device bounded send queue + per-announcement fan-out (PROTOCOL.md §7).
///
/// Honest transport model:
///  - the office mic stream is captured once (the recording writer),
///  - each READY device gets its own bounded queue and its own drain task, so a slow device can
///    never stall the others: enqueue is TryWrite (never blocks) and a device whose queue is full
///    is dropped alone with failed(receive_queue_overflow),
///  - socket delivery is counted as socket delivery only. Audible completion comes from the
///    device's `completed` receipt (plan §12), never from these counters.
/// Counters are in memory; the database is not touched per frame.
public sealed class MediaRouter
{
    private readonly LiveSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<MediaRouter> _log;
    private readonly ConcurrentDictionary<string, StreamGroup> _groups = new();
    private readonly ConcurrentDictionary<string, MicConnection> _mics = new();

    /// Wired in Program.cs (kept as callbacks so MediaRouter never depends on AnnouncementsService).
    public Action<string, string>? OnFirstFrameSent { get; set; }    // (announcementId, deviceId)
    public Action<string, string>? OnQueueOverflow { get; set; }
    public Action<string, string, string>? OnStreamClosed { get; set; } // (announcementId, deviceId, reason)

    public MediaRouter(LiveSettings settings, TimeProvider time, ILogger<MediaRouter> log)
    {
        _settings = settings; _time = time; _log = log;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public int ActiveAnnouncementStreams => _groups.Count;
    public int ActiveMicConnections => _mics.Count;

    private sealed class StreamGroup
    {
        public readonly ConcurrentDictionary<string, DeviceStream> Streams = new();
        public uint Seq;
        public bool Ended;
    }

    public sealed class DeviceStream
    {
        public string AnnouncementId { get; }
        public string DeviceId { get; }
        public string? RecordedPath { get; }         // non-null => file-backed (recorded announcement)
        public int Capacity { get; }
        public bool Overflowed;
        public long FramesQueued;
        public long FramesSent;
        public bool LastSent;
        public WebSocket? Socket;

        private readonly Channel<MediaFrame> _queue;

        public DeviceStream(string announcementId, string deviceId, int capacity, string? recordedPath)
        {
            AnnouncementId = announcementId; DeviceId = deviceId; Capacity = capacity; RecordedPath = recordedPath;
            _queue = Channel.CreateBounded<MediaFrame>(new BoundedChannelOptions(Math.Max(capacity, 1))
            {
                FullMode = BoundedChannelFullMode.Wait,     // TryWrite returns false when full
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public bool TryEnqueue(MediaFrame frame)
        {
            if (Overflowed || !_queue.Writer.TryWrite(frame)) return false;
            FramesQueued++;
            return true;
        }

        public void CompleteWriting() => _queue.Writer.TryComplete();
        public ChannelReader<MediaFrame> Reader => _queue.Reader;
        public int QueueCount => _queue.Reader.Count;
    }

    public sealed class MicConnection
    {
        public string AnnouncementId { get; }
        public string LeaseId { get; }
        public WebSocket Socket { get; }
        public bool OperatorClosing;                  // set when the server closes it because the operator stopped
        public MicConnection(string announcementId, string leaseId, WebSocket socket)
        { AnnouncementId = announcementId; LeaseId = leaseId; Socket = socket; }
    }

    /// Create the per-device queue for a ready device (live) or register a file-backed stream.
    public void OpenStream(string announcementId, string deviceId, string? recordedPath = null)
    {
        var group = _groups.GetOrAdd(announcementId, _ => new StreamGroup());
        group.Streams.AddOrUpdate(deviceId,
            d =>
            {
                _log.LogDebug("media stream opened for device {DeviceId} announcement {AnnouncementId}", d, announcementId);
                return new DeviceStream(announcementId, d, _settings.MediaQueueFrames, recordedPath);
            },
            (_, existing) => existing);
    }

    public DeviceStream? Stream(string announcementId, string deviceId) =>
        _groups.TryGetValue(announcementId, out var g) && g.Streams.TryGetValue(deviceId, out var s) ? s : null;

    public void AttachSocket(string announcementId, string deviceId, WebSocket socket)
    {
        var s = Stream(announcementId, deviceId);
        if (s is null) return;
        s.Socket = socket;
    }

    public MicConnection RegisterMic(string announcementId, string leaseId, WebSocket socket)
    {
        var conn = new MicConnection(announcementId, leaseId, socket);
        _mics[announcementId] = conn;
        return conn;
    }

    public MicConnection? Mic(string announcementId) => _mics.TryGetValue(announcementId, out var c) ? c : null;

    public void UnregisterMic(string announcementId, MicConnection conn)
    {
        if (_mics.TryGetValue(announcementId, out var cur) && ReferenceEquals(cur, conn))
            _mics.TryRemove(announcementId, out _);
    }

    /// Fan one captured frame out to every device queue of this announcement. Server assigns the
    /// sequence number; the office capture timestamp is preserved (PROTOCOL.md §7).
    /// Returns the number of devices that accepted the frame.
    public int Broadcast(string announcementId, ReadOnlyMemory<byte> pcm, ulong captureTsMs)
    {
        if (!_groups.TryGetValue(announcementId, out var group)) return 0;
        uint seq = ++group.Seq;
        var frame = new MediaFrame(seq, captureTsMs, 0, pcm);
        int accepted = 0;
        foreach (var s in group.Streams.Values)
        {
            if (s.RecordedPath is not null) continue;      // recorded streams are file-backed
            if (s.TryEnqueue(frame)) { accepted++; continue; }
            if (!s.Overflowed)
            {
                s.Overflowed = true;
                _log.LogWarning("device {DeviceId} send queue overflowed on announcement {AnnouncementId} after {Sent} frame(s): dropped alone with failed(receive_queue_overflow)",
                    s.DeviceId, announcementId, s.FramesSent);
                OnQueueOverflow?.Invoke(announcementId, s.DeviceId);
                s.CompleteWriting();
                TryCloseSocket(s, WebSocketCloseStatus.PolicyViolation, "receive_queue_overflow");
            }
        }
        return accepted;
    }

    /// End of announcement: mark the group ended, flag the last queued frame, and let each drain
    /// task close its own socket after the valid queued audio is delivered.
    public void EndAnnouncement(string announcementId, bool closeMicToo = true)
    {
        if (!_groups.TryGetValue(announcementId, out var group)) { }
        else
        {
            group.Ended = true;
            uint seq = ++group.Seq;
            foreach (var s in group.Streams.Values)
            {
                if (s.RecordedPath is null)
                {
                    s.TryEnqueue(new MediaFrame(seq, (ulong)NowMs, MediaFrame.FlagLast, Array.Empty<byte>()));
                    s.CompleteWriting();
                }
            }
        }
        if (closeMicToo)
        {
            if (_mics.TryGetValue(announcementId, out var mic))
            {
                mic.OperatorClosing = true;
                _mics.TryRemove(announcementId, out _);
                CloseWebSocket(mic.Socket, WebSocketCloseStatus.NormalClosure, "announcement ended");
            }
        }
    }

    public void CloseDeviceStream(string announcementId, string deviceId, string reason)
    {
        var s = Stream(announcementId, deviceId);
        if (s is null) return;
        s.CompleteWriting();
        TryCloseSocket(s, WebSocketCloseStatus.NormalClosure, reason);
    }

    /// Called by the drain task when a device's media socket finishes or fails.
    public void ReportStreamClosed(DeviceStream s, string reason, bool deliveredLast)
    {
        s.LastSent |= deliveredLast;
        OnStreamClosed?.Invoke(s.AnnouncementId, s.DeviceId, reason);
    }

    public void CountFrame(DeviceStream s)
    {
        s.FramesSent++;
        if (s.FramesSent == 1) OnFirstFrameSent?.Invoke(s.AnnouncementId, s.DeviceId);
    }

    public List<QueueDepth> QueueDepths()
    {
        var list = new List<QueueDepth>();
        foreach (var (annId, g) in _groups)
        {
            var live = g.Streams.Values.Where(s => s.RecordedPath is null).ToList();
            if (live.Count == 0) continue;
            int max = live.Max(s => s.QueueCount);
            double mean = live.Average(s => (double)s.QueueCount);
            list.Add(new QueueDepth(annId, live.Count, max, Math.Round(mean, 2)));
        }
        return list;
    }

    public void Forget(string announcementId)
    {
        _groups.TryRemove(announcementId, out _);
        _mics.TryRemove(announcementId, out _);
    }

    public long StreamFramesTotal(string announcementId) =>
        _groups.TryGetValue(announcementId, out var g) ? g.Streams.Values.Sum(s => s.FramesSent) : 0;

    /// Recorded audio: stream the stored WAV frame by frame straight from disk (no per-device
    /// queue: a slow device slows only its own read, it never holds memory or stalls others).
    public static IEnumerable<MediaFrame> RecordedFrames(string wavPath, ulong baseCaptureTsMs)
    {
        var bytes = File.ReadAllBytes(wavPath);
        var parsed = Wav.Parse(bytes);
        if (parsed is null) yield break;
        var pcm = parsed.Pcm(bytes);
        uint seq = 0;
        const int frameBytes = Protocol.MaxMediaPayloadBytes;       // 320 samples @ 16 kHz = 20 ms
        for (int off = 0; off < pcm.Length; off += frameBytes)
        {
            int len = Math.Min(frameBytes, pcm.Length - off);
            var pcmSlice = pcm.Slice(off, len).ToArray();
            bool last = off + len >= pcm.Length;
            yield return new MediaFrame(++seq, baseCaptureTsMs + (ulong)(off / 2.0 / Protocol.SampleRate * 1000.0),
                last ? MediaFrame.FlagLast : (byte)0, pcmSlice);
        }
        fs.Dispose();
    }

    private static void TryCloseSocket(DeviceStream s, WebSocketCloseStatus status, string reason)
    {
        if (s.Socket is null) return;
        CloseWebSocket(s.Socket, status, reason);
    }

    private static void CloseWebSocket(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        _ = CloseAsync(ws, status, reason);
    }

    private static async Task CloseAsync(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                await ws.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }
}

/// Flood guard (plan §7 / PROTOCOL.md §10): a token bucket sized for the documented burst.
/// 20 ms frames arrive at 50/s; the limit is 60/s by default and a short burst up to
/// mic_frames_per_s * mic_burst_s frames is tolerated before the excess is dropped.
public sealed class FrameRateGuard
{
    private readonly double _tokensPerMs;
    private readonly double _capacity;
    private double _tokens;
    private long _lastMs;
    private long _dropped;

    public FrameRateGuard(int framesPerS, int burstS, long nowMs)
    {
        _tokensPerMs = framesPerS / 1000.0;
        _capacity = framesPerS * burstS;
        _tokens = _capacity;
        _lastMs = nowMs;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool Allow(long nowMs)
    {
        _tokens = Math.Min(_capacity, _tokens + (nowMs - _lastMs) * _tokensPerMs);
        _lastMs = nowMs;
        if (_tokens >= 1) { _tokens -= 1; return true; }
        Interlocked.Increment(ref _dropped);
        return false;
    }
}

using System.Collections.Concurrent;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// Builds bounded in-memory WAV snapshots from the office mic and sends best-effort partial
/// transcripts to currently connected devices. Final transcripts still use the durable job
/// queue and the finalized recording; live updates never delay or enter the audio path.
public sealed class LiveTranscriptionService
{
    private const int PartialIntervalMs = 6_000;
    private const int MinimumPcmBytes = Protocol.SampleRate * 2 * 3; // give the recognizer a phrase, not a word fragment
    private const int MaximumPcmBytes = Protocol.SampleRate * 2 * LiveSettings.RecordedMaxDurationS;

    private sealed class Session
    {
        public readonly object Gate = new();
        public readonly MemoryStream Pcm = new();
        public long LastStartedAt;
        public long TranscribedBytes;
        public int InferenceRunning;
        public volatile bool Active = true;
        public string? LastText;
        public readonly List<string> Parts = [];
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = [];
    private readonly LiveSettings _settings;
    private readonly ITranscriptionEngine _engine;
    private readonly AnnouncementsService _announcements;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveTranscriptionService> _log;

    public LiveTranscriptionService(LiveSettings settings, ITranscriptionEngine engine,
        AnnouncementsService announcements, TimeProvider time, ILogger<LiveTranscriptionService> log)
    {
        _settings = settings;
        _engine = engine;
        _announcements = announcements;
        _time = time;
        _log = log;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public void Start(string announcementId)
    {
        var session = new Session { LastStartedAt = NowMs };
        if (_sessions.TryRemove(announcementId, out var old)) old.Active = false;
        _sessions[announcementId] = session;
        // This also tells targeted clients, without exposing text, that the transcript stays in
        // the office. Sending is direct and ephemeral; an offline computer never gets live text.
        _announcements.PushLiveTranscript(announcementId, null, "starting");
    }

    public void Append(string announcementId, ReadOnlySpan<byte> pcm)
    {
        if (!_sessions.TryGetValue(announcementId, out var session) || !session.Active) return;
        byte[]? snapshot = null;
        long snapshotEnd = 0;
        long now = NowMs;
        lock (session.Gate)
        {
            if (!session.Active) return;
            int remaining = MaximumPcmBytes - checked((int)session.Pcm.Length);
            if (remaining > 0) session.Pcm.Write(pcm[..Math.Min(pcm.Length, remaining)]);
            long unprocessed = session.Pcm.Length - session.TranscribedBytes;
            if (!_settings.TranscriptionEnabled || unprocessed < MinimumPcmBytes ||
                now - session.LastStartedAt < PartialIntervalMs ||
                Interlocked.CompareExchange(ref session.InferenceRunning, 1, 0) != 0) return;
            session.LastStartedAt = now;
            snapshotEnd = session.Pcm.Length;
            snapshot = new byte[checked((int)unprocessed)];
            session.Pcm.GetBuffer().AsSpan(checked((int)session.TranscribedBytes), snapshot.Length).CopyTo(snapshot);
        }
        _ = InferAsync(announcementId, session, snapshot, snapshotEnd);
    }

    public void Complete(string announcementId)
    {
        if (_sessions.TryRemove(announcementId, out var session))
            lock (session.Gate) { session.Active = false; session.Pcm.Dispose(); }
    }

    private async Task InferAsync(string announcementId, Session session, byte[] pcm, long snapshotEnd)
    {
        try
        {
            if (!await _engine.IsAvailableAsync(CancellationToken.None)) return;
            var text = await _engine.TranscribePreviewAsync(Wav.Mono16Bytes(pcm), CancellationToken.None);
            if (!session.Active || string.IsNullOrWhiteSpace(text)) return;
            string? cumulative = null;
            lock (session.Gate)
            {
                if (!session.Active) return;
                session.TranscribedBytes = Math.Max(session.TranscribedBytes, snapshotEnd);
                if (text != "[no speech detected]") session.Parts.Add(text.Trim());
                cumulative = string.Join(" ", session.Parts);
                if (cumulative == session.LastText) return;
                session.LastText = cumulative;
            }
            if (!string.IsNullOrWhiteSpace(cumulative))
                _announcements.PushLiveTranscript(announcementId, cumulative, "live");
        }
        catch (Exception e)
        {
            _log.LogDebug("live transcription pass for {AnnouncementId} failed: {Error}", announcementId, e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref session.InferenceRunning, 0);
        }
    }
}

using System.Text.Json;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class JitterBufferTests
{
    private static MediaFrame Frame(uint seq, byte flags = 0) =>
        new(seq, 1793700000000UL + seq * 20, flags, new byte[640]);

    [Fact]
    public void Defaults_are_300ms_start_and_2000ms_max_start_clamped_to_200_400()
    {
        var jb = new JitterBuffer();
        Assert.Equal(300, jb.StartMs);
        Assert.Equal(2000, jb.MaxMs);
        Assert.Equal(200, new JitterBuffer(startMs: 50).StartMs);    // clamps down to 200
        Assert.Equal(400, new JitterBuffer(startMs: 9999).StartMs);  // clamps up to 400
    }

    [Fact]
    public void Start_delay_gates_playback_then_drains()
    {
        var jb = new JitterBuffer(startMs: 300, maxMs: 2000);
        jb.Push(Frame(0));
        Assert.Null(jb.Dequeue());           // only 20 ms buffered: wait
        for (uint i = 1; i < 15; i++) jb.Push(Frame(i));
        var first = jb.Dequeue();            // 15 frames = 300 ms → start
        Assert.NotNull(first);
        Assert.Equal(0u, first!.Value.Seq);
    }

    [Fact]
    public void Hard_max_drops_oldest_obsolete_audio()
    {
        var jb = new JitterBuffer(startMs: 200, maxMs: 1000);
        for (uint i = 0; i < 100; i++) jb.Push(Frame(i));   // 2000 ms of audio into a 1000 ms bound
        Assert.Equal(50, jb.BufferedFrames);                 // exactly the max kept
        Assert.Equal(50, jb.OverflowDropped);
        Assert.Equal(50u, jb.Drain()[0].Seq);               // oldest survivors are the newest half
    }

    [Fact]
    public void Gap_counts_missing_frames_without_fabricating_audio()
    {
        var jb = new JitterBuffer(startMs: 200);
        jb.Push(Frame(0));
        jb.Push(Frame(1));
        jb.Push(Frame(10));   // 2..9 missing → 8 gap frames counted, none invented
        Assert.Equal(8, jb.GapsDropped);
        Assert.Equal(3, jb.BufferedFrames);
        jb.Drain();
        Assert.Empty(jb.Drain());
    }

    [Fact]
    public void Stale_and_duplicate_frames_are_dropped()
    {
        var jb = new JitterBuffer(startMs: 200);
        jb.Push(Frame(5));
        jb.Push(Frame(5));    // duplicate seq ≤ last buffered? equal seq → treated as stale/dup
        jb.Push(Frame(3));    // old seq
        Assert.Equal(1, jb.BufferedFrames);
        Assert.Equal(2, jb.StaleDropped);
    }

    [Fact]
    public void Drain_on_last_frame_emits_everything_and_blocks_late_arrivals()
    {
        var jb = new JitterBuffer(startMs: 300);
        for (uint i = 0; i < 6; i++) jb.Push(Frame(i, i == 5 ? MediaFrame.FlagLast : (byte)0));
        var all = jb.Drain();
        Assert.Equal(6, all.Count);
        jb.Push(Frame(6));    // after end: dropped, never replayed
        Assert.Empty(jb.Drain());
        Assert.Equal(1, jb.StaleDropped);
    }
}

public class MediaFrameRoundTripTests
{
    [Fact]
    public void Contracts_media_frame_round_trips_through_bytes()
    {
        var pcm = new byte[640];
        for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 0x34; pcm[i + 1] = 0x56; }
        var f = new MediaFrame(42, 1793700123456UL, MediaFrame.FlagLast, pcm);
        var bytes = f.ToBytes();
        Assert.Equal(MediaFrame.HeaderSize + 640, bytes.Length);

        var parsed = MediaFrame.TryParse(bytes);
        Assert.NotNull(parsed);
        Assert.Equal(42u, parsed!.Value.Seq);
        Assert.Equal(1793700123456UL, parsed.Value.CaptureTsMs);
        Assert.True(parsed.Value.IsLast);
        Assert.True(parsed.Value.Pcm.Span.SequenceEqual(pcm));
    }

    [Fact]
    public void Binary_fixture_parses_one_header_plus_320_samples()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "frame_sbmf.bin");
        if (!File.Exists(path))
            path = Path.Combine("..", "..", "..", "..", "..", "contracts", "fixtures", "bin", "frame_sbmf.bin");
        Assert.True(File.Exists(path), $"fixture not found at {path}");
        var parsed = MediaFrame.TryParse(File.ReadAllBytes(path));
        Assert.NotNull(parsed);
        Assert.Equal(640, parsed!.Value.Pcm.Length); // 320 samples
    }

    [Fact]
    public void Malformed_frames_reject()
    {
        Assert.Null(MediaFrame.TryParse("nope"u8.ToArray()));
        var bad = new MediaFrame(1, 1, 0, new byte[640]).ToBytes();
        bad[4] = 2; // wrong version
        Assert.Null(MediaFrame.TryParse(bad));
        Assert.Null(MediaFrame.TryParse(new byte[10]));
    }
}

public class PipeProtocolTests
{
    [Fact]
    public void Lines_round_trip_snake_case()
    {
        var play = new PipeBellPlay("occ-9", "Period 1 start", "/var/db/cache/std-bell.wav", 3, 75, "announcement", 80);
        var line = PipeProtocol.SerializeLine(PipeProtocol.TBellPlay, play);
        Assert.Contains("\"bell_name\":\"Period 1 start\"", line);
        Assert.Contains("\"duration_s\":3", line);

        var msg = PipeProtocol.ParseLine(line);
        Assert.NotNull(msg);
        Assert.Equal(PipeProtocol.TBellPlay, msg!.Type);
        var back = msg.PayloadAs<PipeBellPlay>();
        Assert.Equal(play, back);
    }

    [Fact]
    public void Oversized_line_is_rejected_not_truncated()
    {
        var huge = new PipeBellPlay("o", new string('x', 100_000), "p", 1, 75, "inherit", 0);
        Assert.Throws<InvalidOperationException>(() => PipeProtocol.SerializeLine(PipeProtocol.TBellPlay, huge));
        Assert.Null(PipeProtocol.ParseLine(new string('x', 100_000)));
        Assert.Null(PipeProtocol.ParseLine("{not json"));
    }

    [Fact]
    public void Ann_start_names_policy_volume_explicitly()
    {
        var start = new PipeAnnStart("ann-1", "live", "Office - all endpoints", true, "announcement", 80, 300);
        var line = PipeProtocol.SerializeLine(PipeProtocol.TAnnStart, start);
        Assert.Contains("\"policy_volume\":80", line);
        Assert.Contains("\"source_label\":\"Office - all endpoints\"", line);
        Assert.Contains("\"max_duration_s\":300", line);
    }
}

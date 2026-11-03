using System.Buffers.Binary;

namespace SchoolBell.Contracts;

/// SBMF binary media frame (PROTOCOL.md §7):
/// magic "SBMF" | version u8 | flags u8 | seq u32 | capture_ts_ms u64 | payload_len u16 | PCM s16le
/// All little-endian. payload_len ≤ 640 bytes (320 samples @ 16 kHz mono, 20 ms).
public readonly struct MediaFrame
{
    public const int HeaderSize = 4 + 1 + 1 + 4 + 8 + 2;   // 20
    public const byte FlagLast = 0b0000_0001;

    public uint Seq { get; }
    public ulong CaptureTsMs { get; }
    public byte Flags { get; }
    public ReadOnlyMemory<byte> Pcm { get; }

    public MediaFrame(uint seq, ulong captureTsMs, byte flags, ReadOnlyMemory<byte> pcm)
    {
        if (pcm.Length > Protocol.MaxMediaPayloadBytes || pcm.Length % 2 != 0)
            throw new ArgumentException("PCM must be ≤640 bytes and whole samples");
        Seq = seq; CaptureTsMs = captureTsMs; Flags = flags; Pcm = pcm;
    }

    public bool IsLast => (Flags & FlagLast) != 0;

    public static ReadOnlySpan<byte> MagicBytes => "SBMF"u8;

    public byte[] ToBytes()
    {
        var buf = new byte[HeaderSize + Pcm.Length];
        var span = buf.AsSpan();
        MagicBytes.CopyTo(span);
        span[4] = 1;
        span[5] = Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(span[6..], Seq);
        BinaryPrimitives.WriteUInt64LittleEndian(span[10..], CaptureTsMs);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], (ushort)Pcm.Length);
        Pcm.Span.CopyTo(span[HeaderSize..]);
        return buf;
    }

    public static MediaFrame? TryParse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < HeaderSize || buf.Length > Protocol.MaxMediaFrameBytes) return null;
        if (!buf[..4].SequenceEqual(MagicBytes) || buf[4] != 1) return null;
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(buf[18..]);
        if (len > Protocol.MaxMediaPayloadBytes || buf.Length != HeaderSize + len || len % 2 != 0) return null;
        return new MediaFrame(
            BinaryPrimitives.ReadUInt32LittleEndian(buf[6..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buf[10..]),
            buf[5],
            buf.Slice(HeaderSize, len).ToArray());
    }
}

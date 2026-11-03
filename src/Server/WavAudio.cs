using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SchoolBell.Server;

public sealed record WavFinal(string Sha256, long Bytes, long DurationMs, long Samples);

/// Streaming 16-bit mono WAV writer (plan §9 / PROTOCOL.md §11): a placeholder RIFF header is
/// written first, PCM is appended as it arrives, and Finalize() patches the RIFF/data sizes,
/// flushes, and hashes the finished file. A half-written file never claims a wrong length:
/// sizes are only correct after Finalize().
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly int _sampleRate;
    private readonly int _blockAlign;
    private long _dataBytes;
    private bool _finalized;

    public string Path { get; }
    public long Samples => _dataBytes / _blockAlign;
    public long Bytes => _dataBytes;
    public bool Finalized => _finalized;

    public WavWriter(string path, int sampleRate = 16000, int channels = 1, int bitsPerSample = 16)
    {
        if (channels != 1 || bitsPerSample != 16)
            throw new ArgumentException("the announcement recording store is 16-bit mono only");
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Path = path;
        _sampleRate = sampleRate;
        _blockAlign = channels * bitsPerSample / 8;
        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        WriteHeader(pcmBytes: 0);
    }

    private void WriteHeader(long pcmBytes)
    {
        _fs.Position = 0;
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + pcmBytes));
        "WAVE"u8.CopyTo(h[8..12]);
        "fmt "u8.CopyTo(h[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);          // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);           // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 1);           // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)_sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(_sampleRate * _blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)_blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)pcmBytes);
        _fs.Write(h);
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (_finalized) throw new InvalidOperationException("recording already finalized");
        if (pcm.Length % _blockAlign != 0) throw new ArgumentException("partial samples in PCM write");
        _fs.Position = 44 + _dataBytes;
        _fs.Write(pcm);
        _dataBytes += pcm.Length;
        _fs.Flush();                       // a crashed live session leaves readable audio, not a hole
    }

    /// Patch sizes, hash the finished file, return honest metadata. Idempotent.
    public WavFinal Finalize()
    {
        if (_finalized) return Hash();
        WriteHeader(_dataBytes);
        _fs.Flush();
        _fs.Flush(true);
        _finalized = true;
        return Hash();
    }

    private WavFinal Hash()
    {
        _fs.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = _fs.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, read);
        var hex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        long durationMs = _sampleRate == 0 ? 0 : (long)Math.Round(_dataBytes * 1000.0 / (_sampleRate * _blockAlign));
        return new WavFinal(hex, _dataBytes, durationMs, _dataBytes / _blockAlign);
    }

    public void Dispose()
    {
        try { if (!_finalized) Finalize(); } catch { }
        _fs.Dispose();
    }
}

public sealed record WavInspect(int Format, int Channels, int BitsPerSample, int SampleRate, long DataBytes)
{
    public long DurationMs => (long)Math.Round(DataBytes * 1000.0 / (SampleRate * Channels * BitsPerSample / 8));
}

/// A parsed WAV: metadata plus where the PCM starts in the buffer (uploads are parsed in memory
/// because the upload limit is 25 MB).
public sealed record WavFile(WavInspect Inspect, int DataOffset)
{
    public ReadOnlySpan<byte> Pcm(byte[] file) => file.AsSpan(DataOffset, (int)Inspect.DataBytes);
}

public static class Wav
{
    public static readonly int[] AcceptedSampleRates = [16000, 44100, 48000];
    public const int TargetSampleRate = 16000;

    /// Parse the RIFF chunk chain from a whole file. Null for anything that is not a canonical
    /// PCM WAV. Extra chunks (LIST/INFO/fact) are skipped; a missing data chunk is not a recording.
    /// The reported data size is clamped to what the buffer actually holds, so a lying header
    /// cannot make us read past the upload.
    public static WavFile? Parse(byte[] file)
    {
        if (file.Length < 44) return null;
        if (!file.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !file.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)) < 36) return null;

        int? format = null, channels = null, bits = null, rate = null;
        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(file.AsSpan(pos, 4));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "fmt ")
            {
                if (size < 16 || body + 16 > file.Length) return null;
                format = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 14));
                pos = body + (int)size + (int)(size % 2);
            }
            else if (id == "data")
            {
                if (format is null) return null;
                long avail = file.Length - body;
                long dataBytes = Math.Min(size, avail);
                if (dataBytes <= 0) return null;
                return new WavFile(new WavInspect(format.Value, channels!.Value, bits!.Value, rate!.Value, dataBytes), body);
            }
            else
            {
                if (size < 0 || size > file.Length) return null;   // absurd chunk
                pos = body + (int)size + (int)(size % 2);
            }
        }
        return null;                                                // no data chunk
    }

    /// Validate an uploaded recording: PCM, mono, 16-bit, one of the accepted sample rates,
    /// non-empty and within the 5-minute bound. Returns (parsed, errorDetail).
    public static (WavFile? File, string? Detail) ValidateUpload(byte[] file)
    {
        var parsed = Parse(file);
        if (parsed is null) return (null, "not a valid WAV file (RIFF/WAVE with fmt and data chunks required)");
        var w = parsed.Inspect;
        if (w.Format != 1) return (null, "only uncompressed PCM WAV is accepted");
        if (w.Channels != 1) return (null, "mono audio required (1 channel)");
        if (w.BitsPerSample != 16) return (null, "16-bit signed PCM required");
        if (!Array.Exists(AcceptedSampleRates, r => r == w.SampleRate))
            return (null, $"sample rate {w.SampleRate} Hz is not supported (16000, 44100 or 48000 Hz)");
        if (w.DataBytes <= 0) return (null, "the WAV file has no audio data");
        if (w.DurationMs > LiveSettings.RecordedMaxDurationS * 1000L)
            return (null, $"recording is {w.DurationMs / 1000} s; the limit is {LiveSettings.RecordedMaxDurationS} s");
        return (parsed, null);
    }

    /// Documented resample (PROTOCOL.md §11 "converted server-side"): linear interpolation over
    /// the 16-bit mono PCM. Output sample count = round(inSamples * dstRate / srcRate), so a
    /// 48 kHz 4800-sample clip becomes exactly 1600 samples at 16 kHz.
    public static byte[] ResampleMono16Linear(byte[] pcmLe, int srcRate, int dstRate)
    {
        if (srcRate == dstRate) return pcmLe;
        int inSamples = pcmLe.Length / 2;
        int outSamples = (int)Math.Round(inSamples * (double)dstRate / srcRate);
        var outBuf = new byte[outSamples * 2];
        double step = inSamples > 1 ? (inSamples - 1) / (double)Math.Max(outSamples - 1, 1) : 0;
        for (int i = 0; i < outSamples; i++)
        {
            double pos = i * step;
            int i0 = (int)Math.Floor(pos);
            int i1 = Math.Min(i0 + 1, inSamples - 1);
            double frac = pos - i0;
            short a = BinaryPrimitives.ReadInt16LittleEndian(pcmLe.AsSpan(i0 * 2));
            short b = BinaryPrimitives.ReadInt16LittleEndian(pcmLe.AsSpan(i1 * 2));
            short v = (short)Math.Round(a + (b - a) * frac, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(outBuf.AsSpan(i * 2), v);
        }
        return outBuf;
    }

    /// Write a complete mono 16-bit WAV in one shot (used for stored uploaded recordings).
    public static WavFinal WriteMono16(string path, byte[] pcmLe, int sampleRate)
    {
        using var w = new WavWriter(path, sampleRate);
        w.Write(pcmLe);
        return w.Finalize();
    }

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, read);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

using System.Buffers.Binary;
using System.Text.Json;

namespace SchoolBell.Contracts;

// A small deployment payload appended to the reusable Windows launcher. No server address.
public sealed record DeploymentPayload(string EnrollmentJson, string? CaCertificateBase64);

public static class DeploymentPackage
{
    private static ReadOnlySpan<byte> Magic => "SchoolBellPackage01"u8;
    public const int MaxPayloadBytes = 64 * 1024;

    public static void Append(Stream output, DeploymentPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > MaxPayloadBytes) throw new InvalidDataException("Deployment payload is too large.");
        output.Write(bytes);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        output.Write(length);
        output.Write(Magic);
    }

    public static DeploymentPayload Read(Stream input)
    {
        int footer = Magic.Length + 4;
        if (input.Length < footer) throw new InvalidDataException("This installer has no enrollment package.");
        input.Seek(-footer, SeekOrigin.End);
        Span<byte> length = stackalloc byte[4];
        input.ReadExactly(length);
        var magic = new byte[Magic.Length]; input.ReadExactly(magic);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("This installer has no enrollment package.");
        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size <= 0 || size > MaxPayloadBytes || size > input.Length - footer)
            throw new InvalidDataException("Invalid enrollment package size.");
        input.Seek(-footer - size, SeekOrigin.End);
        var bytes = new byte[size]; input.ReadExactly(bytes);
        var payload = JsonSerializer.Deserialize<DeploymentPayload>(bytes)
            ?? throw new InvalidDataException("Invalid enrollment package.");
        using var enrollment = JsonDocument.Parse(payload.EnrollmentJson);
        if (!enrollment.RootElement.TryGetProperty("token", out var token) || string.IsNullOrWhiteSpace(token.GetString()) ||
            enrollment.RootElement.TryGetProperty("server", out _))
            throw new InvalidDataException("Enrollment package must contain a token and no server address.");
        return payload;
    }
}

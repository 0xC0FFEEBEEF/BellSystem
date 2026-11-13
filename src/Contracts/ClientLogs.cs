using System.Security.Cryptography;

namespace SchoolBell.Contracts;

/// Raw file chunks preserve multiline exceptions and encoding; acknowledged offsets survive reconnects.
public sealed record ClientLogChunk(string Source, string FileId, long Offset, string Encoding, string Data);

public static class ClientLogAuth
{
    public const string Path = "/api/device-logs";
    public const int MaxBodyBytes = 32 * 1024;
    public const int MaxChunkBytes = 8192;
    public static string Nonce(string timestamp, byte[] body) =>
        "POST:" + Path + ":" + timestamp + ":" + Convert.ToHexString(SHA256.HashData(body));
    public static string Proof(string secret, string device, string timestamp, byte[] body) =>
        AuthProof.Compute(secret, Nonce(timestamp, body), device);
}

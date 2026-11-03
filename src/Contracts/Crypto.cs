using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchoolBell.Contracts;

/// Canonical JSON used for manifest signing: UTF-8, no insignificant whitespace,
/// object keys sorted at every level. Deterministic across server and clients.
public static class CanonicalJson
{
    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, ControlEnvelope.Json);
        return Sort(node).ToJsonString(JsonSerializerOptions.Default);
    }

    /// Canonical serialization that omits one property (e.g. Signature).
    public static string SerializeWithout<T>(T value, string omittedProperty)
    {
        var node = JsonSerializer.SerializeToNode(value, ControlEnvelope.Json) as JsonObject;
        if (node is not null) node.Remove(omittedProperty);
        return Sort(node).ToJsonString(JsonSerializerOptions.Default);
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sort(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        _ => node?.DeepClone(),
    };
}

public static class ManifestSigner
{
    /// Server side: sign a manifest with the server-only signing key.
    public static string Sign(ScheduleManifestPayload manifest, byte[] signingKey)
    {
        var canonical = CanonicalJson.SerializeWithout(manifest, "signature");
        var mac = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(mac);
    }

    public static bool Verify(ScheduleManifestPayload manifest, byte[] verifyKey)
    {
        if (string.IsNullOrEmpty(manifest.Signature)) return false;
        var canonical = CanonicalJson.SerializeWithout(manifest, "signature");
        byte[] expected;
        try { expected = Convert.FromBase64String(manifest.Signature); }
        catch (FormatException) { return false; }
        var mac = HMACSHA256.HashData(verifyKey, Encoding.UTF8.GetBytes(canonical));
        return CryptographicOperations.FixedTimeEquals(mac, expected);
    }

    /// Device-side verification key derived from the per-school trust pin
    /// (distributed only via enrollment). HKDF-SHA256, info = "schoolbell-manifest-verify".
    public static byte[] DeriveDeviceVerifyKey(string serverTrustPinHex) =>
        HKDF.Expand(HashAlgorithmName.SHA256, Convert.FromHexString(serverTrustPinHex), 32,
            "schoolbell-manifest-verify"u8.ToArray());
}

/// Stable bell identity (PROTOCOL.md §5): sha256(template_id|period_index|target_key|local_date).
/// Editing time/sound/name keeps the id; the bell moves instead of duplicating.
public static class OccurrenceIds
{
    public static string Compute(long templateId, int periodIndex, string targetKey, string localDate, string kind)
    {
        var material = $"{templateId}|{periodIndex}|{targetKey}|{localDate}|{kind}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// Canonical target key for occurrence identity.
    public static string TargetKey(TargetScope scope) =>
        scope.All == true ? "all"
        : scope.Groups is { Count: > 0 } ? "groups:" + string.Join(",", scope.Groups.Order())
        : scope.Devices is { Count: > 0 } ? "devices:" + string.Join(",", scope.Devices.Order())
        : "none";
}

public static class AuthProof
{
    /// proof = HMAC-SHA256(device_secret, nonce + device_id), hex lowercase.
    public static string Compute(string deviceSecretHex, string nonce, string deviceId)
    {
        var mac = HMACSHA256.HashData(Convert.FromHexString(deviceSecretHex),
            Encoding.UTF8.GetBytes(nonce + deviceId));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static bool Verify(string deviceSecretHex, string nonce, string deviceId, string proofHex)
    {
        byte[] expected;
        try { expected = Convert.FromHexString(proofHex); }
        catch (FormatException) { return false; }
        var actual = HMACSHA256.HashData(Convert.FromHexString(deviceSecretHex),
            Encoding.UTF8.GetBytes(nonce + deviceId));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

/// Publisher signatures for release artifacts (server self-update and client updates).
/// ECDSA P-256 / SHA-256, DER signature (what `openssl dgst -sha256 -sign` produces).
/// The public key is pinned at install time; the private key never touches a school machine.
public static class ReleaseSignature
{
    public static bool Verify(string manifestText, string signatureBase64, string publicKeyPem)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        using var ecdsa = ECDsa.Create();
        try { ecdsa.ImportFromPem(publicKeyPem); }
        catch (Exception) { return false; }
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(manifestText), sig, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
    }

    public static ReleaseManifest? ParseVerified(string manifestText, string signatureBase64, string publicKeyPem)
    {
        if (!Verify(manifestText, signatureBase64, publicKeyPem)) return null;
        try { return JsonSerializer.Deserialize<ReleaseManifest>(manifestText, ControlEnvelope.Json); }
        catch (JsonException) { return null; }
    }

    /// Semantic-ish version compare: numeric dotted parts, a "-suffix" sorts before the release.
    public static int CompareVersions(string a, string b)
    {
        static (int[] Nums, string Suffix) Split(string v)
        {
            var dash = v.IndexOf('-');
            var core = dash < 0 ? v : v[..dash];
            var suffix = dash < 0 ? "" : v[(dash + 1)..];
            var nums = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            return (nums, suffix);
        }
        var (na, sa) = Split(a.TrimStart('v'));
        var (nb, sb) = Split(b.TrimStart('v'));
        for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (sa == sb) return 0;
        if (sa == "") return 1;
        if (sb == "") return -1;
        return string.CompareOrdinal(sa, sb);
    }
}

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

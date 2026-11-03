using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace SchoolBell.Server;

public static class Util
{
    /// Build version (set by build/release.sh via -p:Version); dev builds report Directory.Build.props.
    public static readonly string ServerVersion = ReadVersion();

    private static string ReadVersion()
    {
        var v = typeof(Util).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }

    /// snake_case options for our own API DTOs (Contracts types use ControlEnvelope.Json).
    public static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Same shape as ApiJson but nulls are written out: for the office UI a missing field and a
    /// present-but-null field must not be confused (e.g. "no active lease").
    public static readonly JsonSerializerOptions ApiJsonVisibleNulls = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly JsonSerializerOptions ApiRead = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static string RandomHex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    public static string Sha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    public static bool HexEqualsConstantTime(string aHex, string bHex)
    {
        try
        {
            var a = Convert.FromHexString(aHex);
            var b = Convert.FromHexString(bHex);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException) { return false; }
    }

    /// Walk up from a directory looking for a repo-relative folder (used for legacy/sounds).
    public static string? FindUpwards(string? startDir, params string[] relSegments)
    {
        var dir = startDir is null ? null : new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relSegments).ToArray());
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

/// Uniform error responses: JSON {"detail": ...} with explicit status codes.
public static class Err
{
    public static IResult Detail(int status, string detail) =>
        Results.Json(new { detail }, Util.ApiJson, statusCode: status);
}

public static class Audit
{
    public static void Add(Db db, string actor, string action, string? detail = null)
    {
        using var conn = db.Open();
        Add(conn, null, actor, action, detail);
    }

    /// Write the audit row on an existing connection (and transaction, if given).
    /// Use this while another unit of work is open on the same database; a second
    /// connection would contend with it. Call only after any data reader is closed.
    public static void Add(Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction? tx, string actor, string action, string? detail = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO audit(actor, action, detail, at) VALUES ($a, $ac, $d, $t)";
        cmd.Parameters.AddWithValue("$a", actor);
        cmd.Parameters.AddWithValue("$ac", action);
        cmd.Parameters.AddWithValue("$d", detail ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }
}

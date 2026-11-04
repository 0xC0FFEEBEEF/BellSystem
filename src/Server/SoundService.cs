using System.Security.Cryptography;

namespace SchoolBell.Server;

public sealed record SoundAsset(string Id, string Sha256, long Bytes, string Url);

/// Built-in sounds: seeded once from sounds/ (next to the binary) or assets/sounds into the data dir, sha256-hashed and stored.
/// Sound bytes are served by asset id (documented tradeoff: manifest-referenced public bytes;
/// real delivery authorization rides on the signed manifest, not on the file endpoint).
public sealed class SoundService
{
    public static readonly (string Id, string SourceFile)[] BuiltIn =
    [
        ("std-bell", "bell.wav"),
        ("std-chime", "chime.wav"),
        ("std-buzzer", "buzzer.wav"),
    ];

    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly Microsoft.Extensions.Logging.ILogger<SoundService> _log;

    public SoundService(Db db, ServerConfig cfg, Microsoft.Extensions.Logging.ILogger<SoundService> log)
    {
        _db = db; _cfg = cfg; _log = log;
    }

    public void SeedBuiltIn(string? sourceDir)
    {
        Directory.CreateDirectory(_cfg.SoundsDir);
        foreach (var (id, file) in BuiltIn)
        {
            if (Exists(id)) continue;
            var src = sourceDir is null ? null : Path.Combine(sourceDir, file);
            if (src is null || !File.Exists(src))
            {
                _log.LogWarning("built-in sound source missing for {SoundId}: {File} (publish will omit it)", id, file);
                continue;
            }
            var dest = Path.Combine(_cfg.SoundsDir, $"{id}.wav");
            var tmp = dest + ".tmp";
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
            var bytes = File.ReadAllBytes(dest);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            using var conn = _db.Open();
            conn.Run("""
                INSERT INTO sound_assets(id, sha256, file_path, bytes) VALUES ($i, $s, $p, $b)
                ON CONFLICT(id) DO UPDATE SET sha256 = $s, file_path = $p, bytes = $b
                """,
                ("$i", id), ("$s", sha), ("$p", dest), ("$b", (long)bytes.Length));
            _log.LogInformation("seeded built-in sound {SoundId} ({Bytes} bytes)", id, bytes.Length);
        }
    }

    public bool Exists(string id) => Get(id) is not null;

    public SoundAsset? Get(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, sha256, file_path, bytes FROM sound_assets WHERE id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SoundAsset(r.GetString(0), r.GetString(1), r.IsDBNull(3) ? 0 : r.GetInt64(3), $"/api/sounds/{r.GetString(0)}");
    }

    public List<SoundAsset> List()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, sha256, file_path, bytes FROM sound_assets ORDER BY id";
        var list = new List<SoundAsset>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SoundAsset(r.GetString(0), r.GetString(1), r.IsDBNull(3) ? 0 : r.GetInt64(3), $"/api/sounds/{r.GetString(0)}"));
        return list;
    }

    public (string Path, SoundAsset Asset)? ResolveFile(string id)
    {
        var a = Get(id);
        if (a is null) return null;
        using var conn = _db.Open();
        var path = conn.Scalar("SELECT file_path FROM sound_assets WHERE id = $i", ("$i", id)) as string;
        if (path is null || !File.Exists(path)) return null;
        return (path, a);
    }
}

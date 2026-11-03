using System.Security.Cryptography;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public sealed record SoundCacheResult(bool Success, string? Failure, int Downloaded, int Reused, string CurrentDir);

/// Sound cache with atomic directory swap (PROTOCOL §5 acceptance flow):
/// download into a staging dir → sha256-verify every sound → rename staging to
/// `current`. Any failure leaves the previous cache untouched.
public sealed class SoundCache
{
    private readonly string _root;
    private readonly object _gate = new();

    public SoundCache(string dataDir)
    {
        _root = System.IO.Path.Combine(dataDir, "cache");
        Directory.CreateDirectory(_root);
    }

    public string CurrentDir => System.IO.Path.Combine(_root, "current");

    public string? GetSoundPath(string soundId)
    {
        var name = SafeFileName(soundId);
        if (name is null) return null;
        var p = System.IO.Path.Combine(CurrentDir, name);
        return File.Exists(p) ? p : null;
    }

    public bool HasCache => Directory.Exists(CurrentDir);

    public async Task<SoundCacheResult> FetchAndSwapAsync(
        HttpClient http, IEnumerable<SoundRef> sounds, CancellationToken ct) =>
        await Task.Run(() =>
        {
            lock (_gate)
            {
                var stage = System.IO.Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    return Run(http, sounds, stage, ct);
                }
                finally
                {
                    TryDeleteTree(stage);
                }
            }
        }, ct);

    private SoundCacheResult Run(HttpClient http, IEnumerable<SoundRef> sounds, string stage, CancellationToken ct)
    {
        int downloaded = 0, reused = 0;
        foreach (var s in sounds)
        {
            var name = SafeFileName(s.Id);
            if (name is null)
                return new(false, $"invalid sound id '{s.Id}'", 0, 0, CurrentDir);
            var dest = System.IO.Path.Combine(stage, name);

            // Reuse an already-cached copy when its hash still matches the manifest.
            var existing = System.IO.Path.Combine(CurrentDir, name);
            if (File.Exists(existing) && Sha256Hex(existing) == s.Sha256.ToLowerInvariant())
            {
                File.Copy(existing, dest);
                reused++;
                continue;
            }

            byte[] bytes;
            try
            {
                using var resp = http.GetAsync(s.Url, ct).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    return new(false, $"fetch_failed:{s.Id}:{(int)resp.StatusCode}", 0, 0, CurrentDir);
                bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return new(false, $"fetch_failed:{s.Id}", 0, 0, CurrentDir);
            }
            catch (OperationCanceledException) { throw; }

            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (sha != s.Sha256.ToLowerInvariant())
                return new(false, $"hash_mismatch:{s.Id}", 0, 0, CurrentDir);
            File.WriteAllBytes(dest, bytes);
            downloaded++;
        }

        // Atomic swap: current → old, stage → current, then drop old.
        var old = System.IO.Path.Combine(_root, "old-" + Guid.NewGuid().ToString("N"));
        TryDeleteStaleAuxDirs(except: stage);
        if (Directory.Exists(CurrentDir)) Directory.Move(CurrentDir, old);
        try
        {
            Directory.Move(stage, CurrentDir);
        }
        catch
        {
            if (Directory.Exists(old) && !Directory.Exists(CurrentDir))
                Directory.Move(old, CurrentDir); // roll back so the old cache survives
            throw;
        }
        TryDeleteTree(old);
        return new(true, null, downloaded, reused, CurrentDir);
    }

    /// A sound outside the accepted manifest (a "ring now" with a custom sound): reuse the cached
    /// copy when its hash matches, otherwise download into cache/adhoc and verify sha256 before use.
    public async Task<string?> EnsureSoundAsync(HttpClient http, SoundRef sound, CancellationToken ct)
    {
        var name = SafeFileName(sound.Id);
        if (name is null || string.IsNullOrEmpty(sound.Sha256)) return null;
        var want = sound.Sha256.ToLowerInvariant();
        var cached = System.IO.Path.Combine(CurrentDir, name);
        if (File.Exists(cached) && Sha256Hex(cached) == want) return cached;

        var adhocDir = System.IO.Path.Combine(_root, "adhoc");
        Directory.CreateDirectory(adhocDir);
        var adhoc = System.IO.Path.Combine(adhocDir, $"{System.IO.Path.GetFileNameWithoutExtension(name)}-{want[..16]}.wav");
        if (File.Exists(adhoc) && Sha256Hex(adhoc) == want) return adhoc;

        byte[] bytes;
        try
        {
            using var resp = await http.GetAsync(sound.Url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != want) return null;
        var tmp = adhoc + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, adhoc, overwrite: true);
        // keep the ad-hoc folder small: newest 8 files
        foreach (var old in new DirectoryInfo(adhocDir).GetFiles("*.wav").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8))
            try { old.Delete(); } catch (IOException) { }
        return adhoc;
    }

    private void TryDeleteStaleAuxDirs(string except)
    {
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (dir == except) continue;
            if (name.StartsWith("stage-") || name.StartsWith("old-")) TryDeleteTree(dir);
        }
    }

    private static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    private static string Sha256Hex(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static string? SafeFileName(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            return null;
        return id + ".wav";
    }
}

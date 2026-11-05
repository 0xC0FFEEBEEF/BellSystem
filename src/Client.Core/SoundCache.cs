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
                try
                {
                    Directory.CreateDirectory(stage);
                    return Run(http, sounds, stage, ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Windows: antivirus or another process holding a sound file. Never let it end the
                    // connection: report it; the previous cache stays in use and the manifest is retried.
                    return new(false, $"cache_io: {e.Message}", 0, 0, CurrentDir);
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
            if (File.Exists(existing) && TrySha256Hex(existing) == s.Sha256.ToLowerInvariant()
                && Retry(() => File.Copy(existing, dest, overwrite: true)))
            {
                reused++;
                continue;
            }   // a copy we cannot read is simply downloaded again

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

        // Atomic swap: current → old, stage → current, then drop old. Each move is retried briefly:
        // on Windows a scanner that just opened a new file blocks the rename for a moment.
        var old = System.IO.Path.Combine(_root, "old-" + Guid.NewGuid().ToString("N"));
        TryDeleteStaleAuxDirs(except: stage);
        if (Directory.Exists(CurrentDir) && !Retry(() => Directory.Move(CurrentDir, old)))
            return new(false, "cache_locked: the sound folder is in use and could not be replaced", 0, 0, CurrentDir);
        if (!Retry(() => Directory.Move(stage, CurrentDir)))
        {
            if (Directory.Exists(old) && !Directory.Exists(CurrentDir))
                Retry(() => Directory.Move(old, CurrentDir)); // roll back so the old cache survives
            return new(false, "cache_locked: the new sounds could not be put in place", 0, 0, CurrentDir);
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // left for the next swap
    }

    /// File operations Windows can refuse for a moment (scanner, indexer): a few short retries.
    private static bool Retry(Action op, int attempts = 6, int delayMs = 250)
    {
        for (int i = 1; ; i++)
        {
            try { op(); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (i >= attempts) return false;
                Thread.Sleep(delayMs);
            }
        }
    }

    private static string? TrySha256Hex(string file)
    {
        try { return Sha256Hex(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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

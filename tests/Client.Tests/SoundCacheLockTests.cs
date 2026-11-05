using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

/// A sound folder the computer cannot replace (Windows: a scanner holding a file) must come back as
/// a failure the agent can retry, never as an exception that ends the connection.
public class SoundCacheLockTests
{
    [Fact]
    public async Task A_locked_cache_folder_is_reported_not_thrown_and_the_old_sounds_stay()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;   // chmod cannot lock here
        var dir = TestDirs.Create("cache-lock");
        var root = Path.Combine(dir, "cache");
        try
        {
            var cache = new SoundCache(dir);
            var bytes = System.Text.Encoding.UTF8.GetBytes("BELL");
            Directory.CreateDirectory(cache.CurrentDir);
            File.WriteAllBytes(Path.Combine(cache.CurrentDir, "std-bell.wav"), bytes);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // nothing can be created or moved
            var result = await cache.FetchAndSwapAsync(new HttpClient(), [new SoundRef("std-bell", sha, "/api/sounds/std-bell")], CancellationToken.None);

            Assert.False(result.Success);
            Assert.StartsWith("cache_", result.Failure);
            Assert.NotNull(cache.GetSoundPath("std-bell"));
        }
        finally
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            TestDirs.TryDelete(dir);
        }
    }
}

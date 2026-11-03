using System.Security.Cryptography;
using System.Text;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

/// Fake HTTP handler serving sound bytes for cache tests.
public sealed class StubSoundHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (Files.TryGetValue(path, out var bytes))
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) });
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

public class SoundCacheTests
{
    private static string Sha256(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Atomic_swap_keeps_old_cache_on_hash_mismatch()
    {
        var dir = TestDirs.Create("cache-hash");
        try
        {
            var cache = new SoundCache(dir);
            var good = Encoding.UTF8.GetBytes("ORIGINAL BELL WAV BYTES");
            var handler = new StubSoundHandler { Files = { ["/api/sounds/std-bell"] = good } };
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };

            var first = await cache.FetchAndSwapAsync(http, [new SoundRef("std-bell", Sha256(good), "/api/sounds/std-bell")], default)
                ;
            Assert.True(first.Success);
            var path = cache.GetSoundPath("std-bell")!;
            Assert.Equal(good, File.ReadAllBytes(path));

            // Second revision expects different bytes but the server delivers something
            // whose hash matches NEITHER expectation: download fails verification and
            // the old cache must survive intact.
            var bad = Encoding.UTF8.GetBytes("CORRUPTED OR ATTACKED BYTES");
            var unexpected = Encoding.UTF8.GetBytes("SOMETHING ELSE ENTIRELY");
            handler.Files["/api/sounds/std-bell"] = unexpected;
            var second = await cache.FetchAndSwapAsync(http, [new SoundRef("std-bell", Sha256(bad), "/api/sounds/std-bell")], default)
                ;

            Assert.False(second.Success);
            Assert.StartsWith("hash_mismatch", second.Failure);
            Assert.Equal(good, File.ReadAllBytes(cache.GetSoundPath("std-bell")!)); // OLD CACHE INTACT
            // staging dirs cleaned up: only `current` remains
            Assert.Equal(["current"], Directory.GetDirectories(System.IO.Path.Combine(dir, "cache"))
                .Select(Path.GetFileName).ToArray());
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public async Task Fetch_replaces_cache_atomically_and_reuses_matching_hashes()
    {
        var dir = TestDirs.Create("cache-swap");
        try
        {
            var cache = new SoundCache(dir);
            var v1 = Encoding.UTF8.GetBytes("V1");
            var v2 = Encoding.UTF8.GetBytes("V2");
            var handler = new StubSoundHandler { Files = { ["/api/sounds/std-bell"] = v1 } };
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };

            var firstSwap = await cache.FetchAndSwapAsync(http, [new SoundRef("std-bell", Sha256(v1), "/api/sounds/std-bell")], default);
            Assert.True(firstSwap.Success);

            // Revision 2: new bell bytes + unchanged chime already cached with matching hash.
            var chime = Encoding.UTF8.GetBytes("CHIME");
            File.WriteAllBytes(System.IO.Path.Combine(cache.CurrentDir, "std-chime.wav"), chime);
            handler.Files["/api/sounds/std-bell"] = v2;
            var res = await cache.FetchAndSwapAsync(http, [
                new SoundRef("std-bell", Sha256(v2), "/api/sounds/std-bell"),
                new SoundRef("std-chime", Sha256(chime), "/api/sounds/std-chime"),
            ], default);

            Assert.True(res.Success);
            Assert.Equal(1, res.Downloaded);
            Assert.Equal(1, res.Reused);
            Assert.Equal(v2, File.ReadAllBytes(cache.GetSoundPath("std-bell")!));
            Assert.Equal(chime, File.ReadAllBytes(cache.GetSoundPath("std-chime")!));
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public async Task Missing_url_fails_fetch_and_keeps_previous_cache()
    {
        var dir = TestDirs.Create("cache-missing");
        try
        {
            var cache = new SoundCache(dir);
            var v = Encoding.UTF8.GetBytes("V");
            var handler = new StubSoundHandler { Files = { ["/api/sounds/std-bell"] = v } };
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
            await cache.FetchAndSwapAsync(http, [new SoundRef("std-bell", Sha256(v), "/api/sounds/std-bell")], default)
                ;

            var res = await cache.FetchAndSwapAsync(http, [new SoundRef("std-new", Sha256(v), "/api/sounds/std-new")], default)
                ;
            Assert.False(res.Success);
            Assert.StartsWith("fetch_failed", res.Failure);
            Assert.NotNull(cache.GetSoundPath("std-bell")); // old cache untouched
        }
        finally { TestDirs.TryDelete(dir); }
    }
}

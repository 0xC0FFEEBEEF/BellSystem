using System.Net;
using System.Text;
using System.Text.Json;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class ClientLogUploaderTests
{
    sealed class Receiver : HttpMessageHandler
    {
        public List<ClientLogChunk> Chunks { get; } = [];
        public bool Fail { get; set; }
        public bool LoseReply { get; set; }
        public string Secret { get; } = new('a', 64);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var bytes = await r.Content!.ReadAsByteArrayAsync(ct);
            Assert.True(bytes.Length <= ClientLogAuth.MaxBodyBytes);
            Assert.Equal(ClientLogAuth.Path, r.RequestUri!.AbsolutePath);
            Assert.Equal(ClientLogAuth.Proof(Secret, "device", r.Headers.GetValues("X-SchoolBell-Time").Single(), bytes),
                r.Headers.GetValues("X-SchoolBell-Proof").Single());
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            Chunks.Add(JsonSerializer.Deserialize<ClientLogChunk>(bytes, ControlEnvelope.Json)!);
            if (LoseReply) throw new HttpRequestException("reply lost after storage");
            return new(HttpStatusCode.OK);
        }
    }

    static ClientConfig Config(string dir, Receiver receiver) => new()
    {
        ServerUrl = "https://bells.example", DeviceId = "device", DeviceSecret = receiver.Secret, DataDir = dir,
    };

    [Fact]
    public async Task Outage_restart_append_and_rotation_resume_without_skipping_or_reuploading()
    {
        var dir = TestDirs.Create("log-upload");
        try
        {
            var path = Path.Combine(dir, "service.log");
            await File.WriteAllTextAsync(path, "DEBUG first message\n");
            using var receiver = new Receiver { Fail = true };
            using var http = new HttpClient(receiver);
            var cfg = Config(dir, receiver);
            IEnumerable<ClientLogFile> Files() => new[] { path + ".1", path }.Where(File.Exists).Select(p => new ClientLogFile(p, "service.log"));
            await new ClientLogUploader(cfg, http, Files).UploadPassAsync(default);
            Assert.Empty(receiver.Chunks);
            receiver.Fail = false;
            await new ClientLogUploader(cfg, http, Files).UploadPassAsync(default);
            Assert.Single(receiver.Chunks);
            await File.AppendAllTextAsync(path, "WARN after outage\n");
            await new ClientLogUploader(cfg, http, Files).UploadPassAsync(default);
            Assert.Equal(2, receiver.Chunks.Count);
            Assert.Equal(receiver.Chunks[0].FileId, receiver.Chunks[1].FileId);
            File.Move(path, path + ".1");
            await File.WriteAllTextAsync(path, "INFO new generation\n");
            await new ClientLogUploader(cfg, http, Files).UploadPassAsync(default);
            Assert.Equal(3, receiver.Chunks.Count);
            Assert.NotEqual(receiver.Chunks[0].FileId, receiver.Chunks[2].FileId);
            Assert.Equal(0, receiver.Chunks[2].Offset);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public async Task Lost_first_reply_keeps_stable_id_for_server_deduplication_after_restart()
    {
        var dir = TestDirs.Create("log-lost-reply");
        try
        {
            var path = Path.Combine(dir, "service.log");
            await File.WriteAllTextAsync(path, "INFO reconnecting\n");
            using var receiver = new Receiver { LoseReply = true };
            using var http = new HttpClient(receiver);
            var cfg = Config(dir, receiver);
            ClientLogFile[] Files() => [new(path, "service.log")];
            await Assert.ThrowsAsync<HttpRequestException>(() => new ClientLogUploader(cfg, http, Files).UploadPassAsync(default));
            receiver.LoseReply = false;
            await new ClientLogUploader(cfg, http, Files).UploadPassAsync(default);
            Assert.Equal(receiver.Chunks[0], receiver.Chunks[1]);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Large_verbose_logs_keep_all_bytes_in_bounded_chunks(bool unicode)
    {
        var dir = TestDirs.Create("log-unicode");
        try
        {
            var path = Path.Combine(dir, "companion.log");
            var encoding = unicode ? Encoding.Unicode : new UTF8Encoding(false);
            await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("DEBUG café 🔔\n    exception detail\n", 1000)), encoding);
            using var receiver = new Receiver();
            using var http = new HttpClient(receiver);
            var cfg = Config(dir, receiver);
            var uploader = new ClientLogUploader(cfg, http, () => [new(path, "companion.log")]);
            for (int i = 0; i < 3; i++) await uploader.UploadPassAsync(default);
            var expected = await File.ReadAllBytesAsync(path);
            var actual = receiver.Chunks.SelectMany(c => Convert.FromBase64String(c.Data)).ToArray();
            Assert.Equal(expected, actual);
            Assert.All(receiver.Chunks, c => Assert.True(Convert.FromBase64String(c.Data).Length <= ClientLogAuth.MaxChunkBytes));
            Assert.All(receiver.Chunks, c => Assert.Equal(unicode ? "utf-16" : "utf-8", c.Encoding));
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Long_lines_do_not_split_unicode_characters_between_chunks(bool unicode)
    {
        var dir = TestDirs.Create("log-codepoints");
        try
        {
            var path = Path.Combine(dir, "service.log");
            var encoding = unicode ? Encoding.Unicode : new UTF8Encoding(false);
            var text = new string('x', unicode ? 4094 : 8191) + "🔔" + new string('y', 9000);
            await File.WriteAllTextAsync(path, text, encoding);
            using var receiver = new Receiver();
            using var http = new HttpClient(receiver);
            await new ClientLogUploader(Config(dir, receiver), http, () => [new(path, "service.log")]).UploadPassAsync(default);
            var actual = string.Concat(receiver.Chunks.Select(c => encoding.GetString(Convert.FromBase64String(c.Data)))).TrimStart('\ufeff');
            Assert.Equal(text, actual);
        }
        finally { TestDirs.TryDelete(dir); }
    }
}

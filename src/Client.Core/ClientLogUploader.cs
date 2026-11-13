using System.Globalization;
using System.Text.Json;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

public sealed record ClientLogFile(string Path, string Source);

/// File-backed upload queue. Read only known SchoolBell logs; never configuration or credentials.
/// Files remain the queue until a successful response durably advances the local byte cursor.
public sealed class ClientLogUploader
{
    public sealed class Position
    {
        public string Source { get; set; } = "";
        public long CreatedTicks { get; set; }
        public string Prefix { get; set; } = "";
        public string FileId { get; set; } = Guid.NewGuid().ToString("n");
        public long Offset { get; set; }
        public long SeenAt { get; set; }
    }

    private readonly ClientConfig _cfg;
    private readonly HttpClient _http;
    private readonly Func<IEnumerable<ClientLogFile>> _files;
    private readonly Func<long> _now;
    private readonly JsonStore<List<Position>> _store;
    private readonly List<Position> _positions;

    public ClientLogUploader(ClientConfig cfg, HttpClient http, Func<IEnumerable<ClientLogFile>> files, Func<long>? now = null)
    {
        _cfg = cfg; _http = http; _files = files;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _store = new(Path.Combine(cfg.DataDir, "log-upload-cursors.json"));
        _positions = _store.Load() ?? [];
    }

    /// At most 256 KiB per pass; separate HTTP requests keep audio/control channels free.
    public async Task UploadPassAsync(CancellationToken ct)
    {
        int budget = 32;
        foreach (var file in _files())
        {
            if (budget <= 0) break;
            try
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0) continue;
                var head = new byte[(int)Math.Min(64, stream.Length)];
                stream.ReadExactly(head);
                // Windows preserves creation time across appends/renames. Unix may expose
                // mutable ctime instead; there the stable file prefix identifies a generation.
                var created = OperatingSystem.IsWindows() ? File.GetCreationTimeUtc(file.Path).Ticks : 0;
                var pos = _positions.FirstOrDefault(p => p.Source == file.Source && p.CreatedTicks == created &&
                    p.Offset <= stream.Length && head.AsSpan().StartsWith(Convert.FromBase64String(p.Prefix)));
                if (pos is null)
                {
                    pos = new() { Source = file.Source, CreatedTicks = created, Prefix = Convert.ToBase64String(head) };
                    _positions.Add(pos);
                    _store.Save(_positions); // stable id even if the first response is lost
                }
                pos.SeenAt = _now();
                string encoding = head.Length >= 2 && head[0] == 0xff && head[1] == 0xfe ? "utf-16"
                    : head.Length >= 2 && head[0] == 0xfe && head[1] == 0xff ? "utf-16BE" : "utf-8";
                stream.Position = pos.Offset;
                while (stream.Position < stream.Length && budget-- > 0)
                {
                    var bytes = new byte[ClientLogAuth.MaxChunkBytes];
                    int n = await stream.ReadAsync(bytes, ct);
                    if (n == 0) break;
                    // Prefer complete lines. Keep UTF-8 codepoints / UTF-16 code units together.
                    int length = n;
                    if (stream.Position < stream.Length)
                    {
                        if (encoding == "utf-8")
                        {
                            int newline = Array.LastIndexOf(bytes, (byte)'\n', n - 1, n);
                            if (newline >= 0) length = newline + 1;
                            else
                            {
                                int lead = n - 1;
                                while (lead > 0 && (bytes[lead] & 0xc0) == 0x80) lead--;
                                int expected = bytes[lead] < 0x80 ? 1 : bytes[lead] < 0xe0 ? 2 : bytes[lead] < 0xf0 ? 3 : 4;
                                if (lead + expected > n) length = lead;
                            }
                        }
                        else
                        {
                            length -= length % 2;
                            if (length >= 2)
                            {
                                int unit = encoding == "utf-16" ? bytes[length - 2] | bytes[length - 1] << 8
                                    : bytes[length - 2] << 8 | bytes[length - 1];
                                if (unit is >= 0xd800 and <= 0xdbff) length -= 2;
                            }
                        }
                    }
                    if (length == 0) break;
                    var chunk = new ClientLogChunk(file.Source, pos.FileId, pos.Offset, encoding, Convert.ToBase64String(bytes, 0, length));
                    var body = JsonSerializer.SerializeToUtf8Bytes(chunk, ControlEnvelope.Json);
                    var timestamp = _now().ToString(CultureInfo.InvariantCulture);
                    using var request = new HttpRequestMessage(HttpMethod.Post, _cfg.ServerUrl.TrimEnd('/') + ClientLogAuth.Path);
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new("application/json");
                    request.Headers.Add("X-SchoolBell-Device", _cfg.DeviceId);
                    request.Headers.Add("X-SchoolBell-Time", timestamp);
                    request.Headers.Add("X-SchoolBell-Proof", ClientLogAuth.Proof(_cfg.DeviceSecret, _cfg.DeviceId, timestamp, body));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using var response = await _http.SendAsync(request, timeout.Token);
                    if (!response.IsSuccessStatusCode) return; // older server / outage: retain and retry
                    pos.Offset += length;
                    stream.Position = pos.Offset;
                    _store.Save(_positions);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* try other available logs */ }
        }
        _positions.RemoveAll(p => p.SeenAt < _now() - 30L * 24 * 60 * 60 * 1000);
        _store.Save(_positions);
    }
}

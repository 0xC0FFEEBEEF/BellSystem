// EnrollTool — obtains device credentials from a SchoolBell server (PROTOCOL.md §2)
// and writes the client config file used by SchoolBellService.
// Exit codes: 0 ok, 2 usage, 3 server unreachable/TLS, 4 rejected by server.
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: EnrollTool --server URL --token-file FILE|--token TEXT [--device-name NAME] --out FILE");
    return 2;
}

string server = "", tokenFile = "", token = "", deviceName = Environment.MachineName, outPath = "";
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" when i + 1 < args.Length: server = args[++i]; break;
        case "--token-file" when i + 1 < args.Length: tokenFile = args[++i]; break;
        case "--token" when i + 1 < args.Length: token = args[++i]; break;
        case "--device-name" when i + 1 < args.Length: deviceName = args[++i]; break;
        case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
    }
}
if (server == "" || outPath == "" || (tokenFile == "" && token == "")) return 2;

if (!Uri.TryCreate(server, UriKind.Absolute, out var uu) || (uu.Scheme != "https" && uu.Scheme != "http"))
{
    Console.Error.WriteLine("invalid server URL (expected https://host)"); return 2;
}
// TLS is validated normally against the OS trust store (no bypass anywhere);
// plain http is only permitted for loopback development.
if (uu.Scheme == "http" && uu.Host is not ("localhost" or "127.0.0.1"))
{
    Console.Error.WriteLine("refusing plain http to a non-loopback server: use https://"); return 2;
}

try { token = token != "" ? token.Trim() : File.ReadAllText(tokenFile).Trim(); }
catch (Exception e) { Console.Error.WriteLine($"cannot read token file: {e.Message}"); return 2; }

var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

// TLS is validated normally against the OS trust store (no bypass anywhere);
// plain http is only permitted for loopback development.
var http = new HttpClient();

EnrollResp? resp;
try
{
    var req = new HttpRequestMessage(HttpMethod.Post, server.TrimEnd('/') + "/api/enroll")
    {
        Content = JsonContent.Create(new { token, device_name = deviceName })
    };
    var r = await http.SendAsync(req);
    if (!r.IsSuccessStatusCode)
    {
        var body = await r.Content.ReadAsStringAsync();
        Console.Error.WriteLine($"server rejected enrollment: HTTP {(int)r.StatusCode} {Truncate(body, 300)}");
        return 4;
    }
    resp = await r.Content.ReadFromJsonAsync<EnrollResp>();
}
catch (HttpRequestException e)
{
    Console.Error.WriteLine($"server unreachable (check address and CA trust): {e.Message}");
    return 3;
}
catch (TaskCanceledException)
{
    Console.Error.WriteLine("server unreachable: timeout"); return 3;
}
if (resp is null || string.IsNullOrEmpty(resp.DeviceId) || string.IsNullOrEmpty(resp.DeviceSecret))
{
    Console.Error.WriteLine("malformed enrollment response"); return 4;
}

var config = new ClientConfig(
    server.TrimEnd('/'), resp.DeviceId, resp.DeviceSecret, resp.ServerTrustPin ?? "",
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), deviceName);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
File.WriteAllText(outPath, JsonSerializer.Serialize(config,
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
// Never echo secrets.
Console.WriteLine($"enrolled device {resp.DeviceId} to {server}; config written to {outPath}");
return 0;

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

record EnrollResp(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_secret")] string DeviceSecret,
    [property: JsonPropertyName("server_trust_pin")] string? ServerTrustPin);

record ClientConfig(
    [property: JsonPropertyName("server_url")] string ServerUrl,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_secret")] string DeviceSecret,
    [property: JsonPropertyName("server_trust_pin")] string ServerTrustPin,
    [property: JsonPropertyName("enrolled_at")] long EnrolledAt,
    [property: JsonPropertyName("device_name")] string DeviceName);

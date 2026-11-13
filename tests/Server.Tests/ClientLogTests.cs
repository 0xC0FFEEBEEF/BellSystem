using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

public class ClientLogTests
{
    const string Device = "test-client-logs";
    const string Secret = "ab0123456789abcdab0123456789abcdab0123456789abcdab0123456789abcdab";

    static void Enroll(Db db)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO devices(device_id, name, secret, first_seen) VALUES ($d, 'Classroom A', $s, 0)";
        cmd.Parameters.AddWithValue("$d", Device); cmd.Parameters.AddWithValue("$s", Secret);
        cmd.ExecuteNonQuery();
    }

    static ClientLogChunk Chunk(string text, string? id = null) => new("machine/logs/service.log",
        id ?? Guid.NewGuid().ToString("n"), 0, "utf-8", Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

    static HttpRequestMessage Request(ClientLogChunk chunk, string? secret = null, long? timestamp = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(chunk, ControlEnvelope.Json);
        var at = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
        var r = new HttpRequestMessage(HttpMethod.Post, ClientLogAuth.Path) { Content = new ByteArrayContent(body) };
        r.Headers.Add("X-SchoolBell-Device", Device);
        r.Headers.Add("X-SchoolBell-Time", at);
        r.Headers.Add("X-SchoolBell-Proof", ClientLogAuth.Proof(secret ?? Secret, Device, at, body));
        return r;
    }

    [Fact]
    public async Task Machine_uploads_are_authenticated_idempotent_and_admin_only()
    {
        using var app = new TestApp();
        Enroll(app.Factory.Services.GetRequiredService<Db>());
        using var client = app.Factory.CreateClient();
        var chunk = Chunk("DEBUG scheduler: accepted revision 5\n    exception detail\n");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(ClientLogAuth.Path, new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(chunk, new string('0', 64)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(chunk, timestamp: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(chunk))).StatusCode); // no CSRF needed
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(chunk))).StatusCode); // lost reply / retry
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Get("/api/admin/client-logs")).StatusCode);
        await app.LoginAsAdmin();
        var rows = (await app.Json(await app.Get("/api/admin/client-logs"))).GetProperty("items");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("Classroom A", rows[0].GetProperty("label").GetString());
        Assert.Contains("exception detail", rows[0].GetProperty("text").GetString());
        var download = await (await app.Get("/api/admin/client-logs.txt")).Content.ReadAsStringAsync();
        Assert.Contains("accepted revision 5", download);
        Assert.DoesNotContain(Secret, download);
        await app.CreateUser("viewer", "password", "viewer");
        await app.LoginAs("viewer", "password");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Get("/api/admin/client-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Get("/api/admin/client-logs.txt")).StatusCode);
    }

    [Fact]
    public async Task Tampering_revocation_and_oversized_uploads_are_rejected()
    {
        using var app = new TestApp();
        var db = app.Factory.Services.GetRequiredService<Db>();
        Enroll(db);
        using var client = app.Factory.CreateClient();
        using var tampered = Request(Chunk("original"));
        tampered.Content = new StringContent("{}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(tampered)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PostAsync(ClientLogAuth.Path, new StringContent(new string('x', ClientLogAuth.MaxBodyBytes + 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request(Chunk(new string('x', ClientLogAuth.MaxChunkBytes + 1))))).StatusCode);
        using (var c = db.Open()) Db.Execute(c, "UPDATE devices SET revoked = 1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(Chunk("revoked")))).StatusCode);
    }

    [Fact]
    public void Filtering_pagination_unicode_and_retention_keep_server_logs_separate()
    {
        using var env = new TestEnv();
        Enroll(env.Database);
        var logs = new ClientLogsService(env.Database, env.Time);
        Assert.True(logs.Ingest(Device, Chunk("old")));
        env.Time.Now = env.Time.Now.AddDays(15);
        Assert.True(logs.Ingest(Device, Chunk("DEBUG bell café 🔔\n")));
        Assert.True(logs.Ingest(Device, Chunk("WARN speaker unavailable\n")));
        Assert.Single(logs.Read(Device, "café", 100));
        Assert.Empty(logs.Read("other-device", null, 100));
        var page = logs.Read(null, null, 1);
        Assert.Single(page);
        Assert.Single(logs.Read(null, null, 1, page[0].Id));
        logs.Retain();
        Assert.Equal(2, logs.Read(null, null, 100).Count);
        Assert.DoesNotContain("café", env.Logs.AllText); // raw client text never enters server ILogger
    }
}

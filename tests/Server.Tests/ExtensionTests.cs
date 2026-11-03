using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

/// v1.1 additions: ring now, device settings, sound library, client update trust, backups.
public class ExtensionTests
{
    [Fact]
    public async Task Ring_now_reaches_the_device_and_records_its_receipt()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var dev = await LiveTest.EnrollAsync(app, "room-101");
        await using var client = await ControlClient.ConnectAsync(app, dev);
        await client.DrainAsync();

        var res = await app.Post("/api/bells/ring", new { target = new { all = true }, bell_name = "Fire drill bell" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var ring = await app.Json(res);
        Assert.Equal(1, ring.GetProperty("device_count").GetInt32());

        ControlEnvelope? got = null;
        for (int i = 0; i < 10 && got is null; i++)
            got = (await client.DrainAsync()).FirstOrDefault(e => e.Type == Protocol.TBellRing);
        Assert.NotNull(got);
        var payload = PayloadSerializer.ToPayload<BellRingPayload>(got!)!;
        Assert.Equal("Fire drill bell", payload.BellName);
        Assert.Equal("std-bell", payload.Sound.Id);
        Assert.Equal(64, payload.Sound.Sha256.Length);
        Assert.True(payload.ExpiresAt - payload.IssuedAt == Protocol.ManualRingTtlSeconds * 1000L);

        await client.SendAsync(ControlEnvelope.Create(Protocol.TReceipt, PayloadSerializer.Node(
            new ReceiptPayload(payload.RingId, ReceiptState.Completed, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null))));
        var rings = await LiveTest.PollJsonAsync(app, "/api/bells/rings?limit=1",
            j => j[0].GetProperty("counts").TryGetProperty("completed", out _));
        Assert.Equal(1, rings[0].GetProperty("counts").GetProperty("completed").GetInt32());
    }

    [Fact]
    public async Task Ring_now_enforces_role_and_scope()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await LiveTest.EnrollAsync(app, "room-102");
        var g = await LiveTest.CreateGroupAsync(app, "Building A");

        Assert.Equal(HttpStatusCode.NotFound, (await app.Post("/api/bells/ring", new { target = new { all = true }, sound_id = "no-such-sound" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post("/api/bells/ring", new { })).StatusCode);

        await app.CreateUser("op", "operator-password-1", "operator", [g.GetProperty("id").GetInt64()], schoolwide: false);
        await app.CreateUser("view", "viewer-password-12", "viewer");
        using var op = new TestApp.Session(app);
        await op.LoginAs("op", "operator-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Post("/api/bells/ring", new { target = new { all = true } })).StatusCode);
        using var viewer = new TestApp.Session(app);
        await viewer.LoginAs("view", "viewer-password-12");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post("/api/bells/ring", new { target = new { all = true } })).StatusCode);
    }

    [Fact]
    public async Task Device_settings_and_group_changes_are_pushed_with_growing_versions()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var dev = await LiveTest.EnrollAsync(app, "room-103");
        await using var client = await ControlClient.ConnectAsync(app, dev);
        await client.SendAsync(ControlEnvelope.Create(Protocol.TDeviceResync, PayloadSerializer.Node(new DeviceResyncPayload(null, 0, []))));
        var s0 = await NextSettings(client);
        Assert.Null(s0.OutputDevice);

        var put = await app.Put($"/api/admin/devices/{dev.Id}/settings", new { output_device = "Classroom Speakers", announcement_volume = 70, volume_cap = 90 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var s1 = await NextSettings(client);
        Assert.Equal("Classroom Speakers", s1.OutputDevice);
        Assert.Equal(70, s1.AnnouncementVolume);
        Assert.True(s1.SettingsVersion > s0.SettingsVersion);

        var g = await LiveTest.CreateGroupAsync(app, "Gym");
        await LiveTest.AddDeviceToGroupAsync(app, g.GetProperty("id").GetInt64(), dev.Id);
        var s2 = await NextSettings(client);
        Assert.Contains(g.GetProperty("id").GetInt64(), s2.Groups);
        Assert.True(s2.SettingsVersion > s1.SettingsVersion);

        Assert.Equal(HttpStatusCode.OK, (await app.Put($"/api/admin/devices/{dev.Id}/settings", new { use_default_output = true })).StatusCode);
        var s3 = await NextSettings(client);
        Assert.Null(s3.OutputDevice);
    }

    private static async Task<DeviceSettingsPayload> NextSettings(ControlClient c)
    {
        for (int i = 0; i < 50; i++)
        {
            var e = await c.NextAsync(10_000);
            Assert.NotNull(e);
            if (e!.Type == Protocol.TDeviceSettings) return PayloadSerializer.ToPayload<DeviceSettingsPayload>(e)!;
        }
        throw new Xunit.Sdk.XunitException("no device.settings received");
    }

    [Fact]
    public async Task Sound_library_validates_uploads_and_protects_built_ins()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var bad = await app.Upload("/api/sounds", "file", "x.wav", "not a wav"u8.ToArray(), ("name", "Bad"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var wav = LiveTest.BuildWav(44100, 44100);       // 1 s, 44.1 kHz mono
        var ok = await app.Upload("/api/sounds", "file", "lunch.wav", wav, ("name", "Lunch bell"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var id = (await app.Json(ok)).GetProperty("id").GetString()!;
        var list = await app.Json(await app.Get("/api/sounds"));
        Assert.Contains(list.EnumerateArray(), s => s.GetProperty("id").GetString() == id && s.GetProperty("duration_ms").GetInt64() == 1000);

        var before = list.EnumerateArray().First(s => s.GetProperty("id").GetString() == "std-bell").GetProperty("sha256").GetString();
        var replaced = await app.Upload("/api/sounds/std-bell", "file", "new-bell.wav", LiveTest.BuildWav(48000, 24000, value: 2000), method: "PUT");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var after = (await app.Json(await app.Get("/api/sounds"))).EnumerateArray().First(s => s.GetProperty("id").GetString() == "std-bell").GetProperty("sha256").GetString();
        Assert.NotEqual(before, after);

        Assert.Equal(HttpStatusCode.Conflict, (await app.Delete("/api/sounds/std-bell")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Delete($"/api/sounds/{id}")).StatusCode);

        await app.CreateUser("view2", "viewer-password-12", "viewer");
        using var viewer = new TestApp.Session(app);
        await viewer.LoginAs("view2", "viewer-password-12");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Upload("/api/sounds", "file", "v.wav", wav)).StatusCode);
    }

    [Fact]
    public async Task Client_update_upload_with_a_bad_signature_is_refused()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var manifest = """{"commit":"x","created_at":1,"files":{"windows/SchoolBellSetup.exe":{"bytes":3,"sha256":"00"}},"product":"SchoolBell","version":"9.9.9"}""";
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "setup", "SchoolBellSetup.exe");
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(manifest)), "manifest", "release-manifest.json");
        form.Add(new ByteArrayContent("MEUCIQDfake"u8.ToArray()), "signature", "release-manifest.json.sig");
        var res = await app.SendMultipart("/api/admin/updates/client/upload", form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("signature", (await app.Json(res)).GetProperty("detail").GetString());
        var overview = await app.Json(await app.Get("/api/admin/updates/client"));
        Assert.Equal(0, overview.GetProperty("releases").GetArrayLength());
    }

    [Fact]
    public async Task Backup_is_consistent_and_restore_staging_validates_it()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await LiveTest.EnrollAsync(app, "room-104");
        var made = await app.Post("/api/admin/backup", new { });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var file = (await app.Json(made)).GetProperty("file").GetString()!;
        var cfg = app.Factory.Services.GetRequiredService<ServerConfig>();
        var path = Backup.Resolve(cfg, file)!;
        var (ok, detail) = Backup.StageRestore(cfg, path);
        Assert.True(ok, detail);
        Assert.True(File.Exists(Path.Combine(cfg.DataDir, "restore-pending", "READY")));
        Directory.Delete(Path.Combine(cfg.DataDir, "restore-pending"), true);

        var junk = Path.Combine(Backup.Dir(cfg), "junk.tar.gz");
        File.WriteAllBytes(junk, [1, 2, 3, 4]);
        Assert.False(Backup.StageRestore(cfg, junk).Ok);
        Assert.Null(Backup.Resolve(cfg, "../etc/passwd.tar.gz"));
    }

    [Fact]
    public async Task Reenrolling_a_computer_keeps_its_record_but_drops_its_old_outbox()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var first = await LiveTest.EnrollAsync(app, "room-105");
        var rec = await app.Upload("/api/announcements/recorded", "file", "a.wav", LiveTest.BuildWav(16000, 16000), ("target", "{\"all\":true}"));
        Assert.Equal(HttpStatusCode.Created, rec.StatusCode);
        var db = LiveTest.DbOf(app);
        int Queued(string id) { using var c = db.Open(); return c.ScalarInt("SELECT COUNT(*) FROM outbox WHERE device_id = $d", ("$d", id)); }
        Assert.True(Queued(first.Id) > 0);

        var again = await LiveTest.EnrollAsync(app, "room-105");      // reinstall: same computer name
        Assert.Equal(first.Id, again.Id);                              // groups/room/settings are kept
        Assert.NotEqual(first.Secret, again.Secret);                   // the old install's credential is dead
        Assert.Equal(0, Queued(again.Id));                             // and nothing old is replayed to the new install
    }

    [Fact]
    public async Task Health_reports_the_build_version_without_authentication()
    {
        using var app = new TestApp();
        var res = await app.SendBare("GET", "/api/health", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Util.ServerVersion, (await res.Content.ReadAsJsonElement()).GetProperty("version").GetString());
    }
}

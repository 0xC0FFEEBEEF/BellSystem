using System.Net;

namespace Server.Tests;

public class AuthHttpTests
{
    [Fact]
    public async Task Me_requires_authentication()
    {
        using var app = new TestApp();
        var res = await app.Get("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Setup_wrong_code_403_then_success_201_then_consumed_403()
    {
        using var app = new TestApp();
        await app.FetchCsrf();
        var bad = await app.Post("/api/auth/setup", new { code = "not-the-code", username = "admin", password = "correct-horse99!" });
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);

        var ok = await app.Post("/api/auth/setup", new { code = TestApp.SetupCode, username = "admin", password = "correct-horse99!" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var again = await app.Post("/api/auth/setup", new { code = TestApp.SetupCode, username = "admin2", password = "correct-horse99!" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task Writes_without_csrf_header_are_401()
    {
        using var app = new TestApp();
        var res = await app.SendBare("POST", "/api/auth/login", new { username = "x", password = "y" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var json = await app.Json(res);
        Assert.Contains("csrf", json.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Setup_weak_password_is_400()
    {
        using var app = new TestApp();
        await app.FetchCsrf();
        var res = await app.Post("/api/auth/setup", new { code = TestApp.SetupCode, username = "admin", password = "short9!" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Login_rate_limit_returns_429_after_8_failures()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await app.CreateUser("slowpoke", "correct-horse99!", "viewer");
        for (int i = 0; i < 8; i++)
        {
            var res = await app.Post("/api/auth/login", new { username = "slowpoke", password = "wrong-password-xx" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        var blocked = await app.Post("/api/auth/login", new { username = "slowpoke", password = "wrong-password-xx" });
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
    }

    [Fact]
    public async Task Logout_invalidates_session()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        Assert.Equal(HttpStatusCode.OK, (await app.Get("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Post("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Get("/api/auth/me")).StatusCode);
    }
}

public class RoleMatrixTests
{
    [Fact]
    public async Task Viewer_cannot_write_schedules_but_can_read()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        await app.CreateUser("view1", "correct-horse99!", "viewer");
        await app.LoginAs("view1", "correct-horse99!");

        var read = await app.Get("/api/schedules");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await app.Post("/api/schedules", new
        {
            name = "Hack",
            target = new { all = true },
            periods = new[] { new { name = "P", start_hm = "08:30", end_hm = "09:20", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
            weekdays = new[] { 1 },
        });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var publish = await app.Post("/api/schedules/publish", null);
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
    }

    [Fact]
    public async Task Operator_out_of_scope_write_and_publish_are_403_in_scope_is_allowed()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        var g3 = await app.Json(await app.Post("/api/admin/groups", new { name = "Group 3" }));
        var g4 = await app.Json(await app.Post("/api/admin/groups", new { name = "Group 4" }));
        await app.CreateUser("oper1", "correct-horse99!", "operator", groups: [g3.GetProperty("id").GetInt32()]);

        await app.LoginAs("oper1", "correct-horse99!");

        var inScope = await app.Post("/api/schedules", new
        {
            name = "InScope",
            target = new { groups = new[] { g3.GetProperty("id").GetInt32() } },
            periods = new[] { new { name = "P", start_hm = "08:30", end_hm = "09:20", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
            weekdays = new[] { 1, 2, 3, 4, 5 },
        });
        Assert.Equal(HttpStatusCode.Created, inScope.StatusCode);

        var outScope = await app.Post("/api/schedules", new
        {
            name = "OutScope",
            target = new { groups = new[] { g4.GetProperty("id").GetInt32() } },
            periods = new[] { new { name = "P", start_hm = "08:30", end_hm = "09:20", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
            weekdays = new[] { 1 },
        });
        Assert.Equal(HttpStatusCode.Forbidden, outScope.StatusCode);

        var allTargets = await app.Post("/api/schedules", new
        {
            name = "AllNoPerm",
            target = new { all = true },
            periods = new[] { new { name = "P", start_hm = "08:30", end_hm = "09:20", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
            weekdays = new[] { 1 },
        });
        Assert.Equal(HttpStatusCode.Forbidden, allTargets.StatusCode); // target.all needs schoolwide_broadcast

        await app.LoginAs("admin", "correct-horse99!");
        var g4res = await app.Post("/api/schedules", new
        {
            name = "AdminG4",
            target = new { groups = new[] { g4.GetProperty("id").GetInt32() } },
            periods = new[] { new { name = "P", start_hm = "08:30", end_hm = "09:20", warn_min = 0, sound_id = "std-bell", volume_policy = "announcement", duration_s = 3 } },
            weekdays = new[] { 1 },
        });
        Assert.Equal(HttpStatusCode.Created, g4res.StatusCode);

        // publish blocked for the operator because the draft now contains an out-of-scope template
        await app.LoginAs("oper1", "correct-horse99!");
        var publish = await app.Post("/api/schedules/publish", null);
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);

        // the admin (who can see everything) may publish the same draft
        await app.LoginAs("admin", "correct-horse99!");
        var adminPublish = await app.Post("/api/schedules/publish", null);
        Assert.Equal(HttpStatusCode.OK, adminPublish.StatusCode);
    }

    [Fact]
    public async Task Schoolwide_operator_can_publish_and_admin_publish_needs_timezone()
    {
        using var noTz = new TestApp();
        await noTz.LoginAsAdmin();
        var res = await noTz.Post("/api/schedules/publish", null);
        Assert.Equal((HttpStatusCode)409, res.StatusCode);
        var detail = (await noTz.Json(res)).GetProperty("detail").GetString()!;
        Assert.Equal("timezone not configured", detail);

        using var app = new TestApp();
        await app.LoginAsAdmin();
        await app.CreateUser("operwide", "correct-horse99!", "operator", groups: [], schoolwide: true);
        var settings = await app.Put("/api/admin/settings", new { school_tz = "America/Chicago" });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        await app.LoginAs("operwide", "correct-horse99!");
        var publish = await app.Post("/api/schedules/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var json = await app.Json(publish);
        Assert.Equal(1, json.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Admin_settings_validate_timezone()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var bad = await app.Put("/api/admin/settings", new { school_tz = "Mars/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var good = await app.Put("/api/admin/settings", new { school_tz = "Europe/Berlin" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }
}

public class HttpSurfaceTests
{
    [Fact]
    public async Task Enroll_response_shape_is_exact_and_needs_no_office_auth()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var tokRes = await app.Post("/api/admin/enrollment-tokens", new { label = "shape", ttl_hours = 24, max_uses = 1 });
        var token = (await app.Json(tokRes)).GetProperty("token").GetString()!;

        // bare call: no cookies, no CSRF (installer-style machine client)
        var res = await app.SendBare("POST", "/api/enroll", new { token, device_name = "rhs-shape" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await app.Json(res);
        Assert.Equal(3, json.EnumerateObject().Count());
        Assert.True(Guid.TryParse(json.GetProperty("device_id").GetString(), out _));
        Assert.Equal(64, json.GetProperty("device_secret").GetString()!.Length);
        Assert.Equal(64, json.GetProperty("server_trust_pin").GetString()!.Length);
    }

    [Fact]
    public async Task Token_list_never_contains_token_values()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var created = await app.Post("/api/admin/enrollment-tokens", new { label = "hidden", ttl_hours = 24, max_uses = 1 });
        var token = (await app.Json(created)).GetProperty("token").GetString()!;
        var listRes = await app.Get("/api/admin/enrollment-tokens");
        var text = await listRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, text);
        Assert.Contains("label", text);
    }

    [Fact]
    public async Task Sounds_are_served_and_sha_mismatch_is_rejected()
    {
        using var app = new TestApp();
        var res = await app.SendBare("GET", "/api/sounds/std-bell", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 44); // WAV header at least
        Assert.Equal("audio/wav", res.Content.Headers.ContentType?.MediaType);

        var bad = await app.SendBare("GET", "/api/sounds/std-bell?sha=0000000000000000000000000000000000000000000000000000000000000000", null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var missing = await app.SendBare("GET", "/api/sounds/nope", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Announcement_and_media_endpoints_are_honest_501_stubs()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        foreach (var path in new[] { "/api/announcements", "/api/announcements/lease" })
        {
            var res = await app.Post(path, null);
            Assert.Equal((HttpStatusCode)501, res.StatusCode);
            Assert.Contains("S2", (await app.Json(res)).GetProperty("detail").GetString()!);
        }
        var media = await app.SendBare("GET", "/ws/media", null);
        Assert.Equal((HttpStatusCode)501, media.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_expose_every_queue_and_limit()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var res = await app.Get("/api/diagnostics");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await app.Json(res);
        Assert.Equal(1000, json.GetProperty("max_connections_configured").GetInt32());
        Assert.Equal(500, json.GetProperty("outbox_cap_configured").GetInt32());
        Assert.True(json.GetProperty("db_size_bytes").GetInt64() > 0);
        Assert.True(json.GetProperty("uptime_seconds").GetInt64() >= 0);
        Assert.Equal(0, json.GetProperty("active_control_connections").GetInt32());
        Assert.Equal("", json.GetProperty("school_tz").GetString()); // default TestApp: no tz configured

        var anon = await app.SendBare("GET", "/api/diagnostics", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }

    [Fact]
    public async Task Device_list_and_acceptance_endpoints_work_after_enrollment()
    {
        using var app = new TestApp(tz: "America/Chicago");
        await app.LoginAsAdmin();
        var tok = (await app.Json(await app.Post("/api/admin/enrollment-tokens", new { label = "devs", ttl_hours = 24, max_uses = 2 })))
            .GetProperty("token").GetString()!;
        await app.SendBare("POST", "/api/enroll", new { token = tok, device_name = "rhs-100" });
        await app.SendBare("POST", "/api/enroll", new { token = tok, device_name = "rhs-101" });

        var devices = await app.Json(await app.Get("/api/devices?q=rhs-100"));
        Assert.Equal(1, devices.GetProperty("total").GetInt32());
        var label = devices.GetProperty("items")[0].GetProperty("label").GetString()!;
        Assert.Equal("No signed-in user (rhs-100)", label);

        var acceptance = await app.Json(await app.Get("/api/schedules/acceptance"));
        Assert.Equal(0, acceptance.GetProperty("latest_revision").GetInt32());
        Assert.Equal(2, acceptance.GetProperty("devices").GetArrayLength());
    }
}

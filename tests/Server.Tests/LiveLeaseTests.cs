using System.Net;
using System.Text.Json;
using SchoolBell.Server;

namespace Server.Tests;

/// Talk lease (PROTOCOL.md §7 / plan §6): creation, renewal, hard cap, expiry, one-lease-per-school
/// conflict, and the role/group-scope gate on every lease and announce call.
public class LiveLeaseTests
{
    [Fact]
    public async Task Lease_create_renew_release_and_second_attempt_is_409()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();

        var createRes = await app.Post("/api/announcements/lease", new { max_duration_s = 60, target = new { all = true } });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await app.Json(createRes);
        var leaseId = created.GetProperty("lease_id").GetString()!;
        long createdAt = created.GetProperty("created_at").GetInt64();
        long expiresAt = created.GetProperty("expires_at").GetInt64();
        Assert.Equal(60_000, expiresAt - createdAt);                       // expires_at = start + max
        Assert.Equal(60, created.GetProperty("renew_interval_s").GetInt32());
        Assert.Equal("admin", created.GetProperty("holder").GetString());
        Assert.Equal("active", created.GetProperty("status").GetString());

        // one active lease schoolwide in the pilot
        var conflict = await app.Post("/api/announcements/lease", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var detail = (await app.Json(conflict)).GetProperty("detail").GetString()!;
        Assert.StartsWith("lease held by admin since ", detail);

        // renew pushes the renew deadline, never past the hard cap
        var renewed = await app.Json(await app.Post($"/api/announcements/lease/{leaseId}/renew", new { }));
        Assert.True(renewed.GetProperty("renew_deadline").GetInt64() > createdAt);
        Assert.Equal(expiresAt, renewed.GetProperty("expires_at").GetInt64());   // hard cap unchanged

        var overCap = await app.Post($"/api/announcements/lease/{leaseId}/renew", new { extend_s = 100_000 });
        Assert.Equal(HttpStatusCode.BadRequest, overCap.StatusCode);
        Assert.Contains("max_duration_s", (await app.Json(overCap)).GetProperty("detail").GetString()!);

        var released = await app.Json(await app.Delete($"/api/announcements/lease/{leaseId}"));
        Assert.True(released.GetProperty("ok").GetBoolean());
        var active = await app.Json(await app.Get("/api/announcements/lease/active"));
        Assert.True(active.GetProperty("lease").ValueKind is JsonValueKind.Null);

        // and the slot is free again
        var again = await app.Post("/api/announcements/lease", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Lease_renew_on_a_released_or_unknown_lease_is_404_or_410_not_a_silent_success()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var created = await app.Json(await app.Post("/api/announcements/lease", new { max_duration_s = 30 }));
        var leaseId = created.GetProperty("lease_id").GetString()!;
        await app.Delete($"/api/announcements/lease/{leaseId}");

        var renew = await app.Post($"/api/announcements/lease/{leaseId}/renew", new { });
        Assert.Equal(HttpStatusCode.Gone, renew.StatusCode);
        Assert.Contains("expired", (await app.Json(renew)).GetProperty("detail").GetString()!);

        var unknown = await app.Post($"/api/announcements/lease/{Guid.NewGuid()}/renew", new { });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var unknownDelete = await app.Delete($"/api/announcements/lease/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknownDelete.StatusCode);
    }

    [Fact]
    public async Task Lease_duration_is_bounded_by_the_settings_cap_30_to_900_seconds()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        LiveTest.SetSetting(app, "talk_lease_max_s", "120");

        var tooLong = await app.Post("/api/announcements/lease", new { max_duration_s = 600 });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("talk_lease_max_s (120 s)", (await app.Json(tooLong)).GetProperty("detail").GetString()!);

        var tooShort = await app.Post("/api/announcements/lease", new { max_duration_s = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var ok = await app.Json(await app.Post("/api/announcements/lease", new { }));
        Assert.Equal(120, ok.GetProperty("max_duration_s").GetInt32());       // default = settings value
        await app.Delete($"/api/announcements/lease/{ok.GetProperty("lease_id").GetString()}");

        // the setting itself is clamped to the protocol window 30..900
        LiveTest.SetSetting(app, "talk_lease_max_s", "5000");
        var clamped = await app.Json(await app.Post("/api/announcements/lease", new { }));
        Assert.Equal(900, clamped.GetProperty("max_duration_s").GetInt32());
    }

    [Fact]
    public async Task Lease_expires_without_renewal_and_ends_the_live_announcement_with_ended_by_lease()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var device = await LiveTest.EnrollAsync(app, "rhs-lease1");
        LiveTest.SetSetting(app, "talk_lease_renew_interval_s", "1");
        LiveTest.SetSetting(app, "preparation_window_s", "1");
        LiveTest.SetSetting(app, "live_offer_ttl_s", "30");

        var created = await app.Json(await app.Post("/api/announcements/lease", new { max_duration_s = 300 }));
        var leaseId = created.GetProperty("lease_id").GetString()!;
        var live = await app.Json(await app.Post("/api/announcements/live", new { target = new { all = true } }));
        var annId = live.GetProperty("announcement_id").GetString()!;

        // nobody renews: the sweep expires the lease and ends the announcement (PROTOCOL.md §7)
        var ended = await LiveTest.PollJsonAsync(app, $"/api/announcements/{annId}",
            j => j.GetProperty("status").GetString() == "ended");
        Assert.Equal("lease", ended.GetProperty("ended_by").GetString());

        var renew = await app.Post($"/api/announcements/lease/{leaseId}/renew", new { });
        Assert.Equal(HttpStatusCode.Gone, renew.StatusCode);

        var leaseJson = await app.Json(await app.Get("/api/announcements/lease/active"));
        Assert.True(leaseJson.GetProperty("lease").ValueKind is JsonValueKind.Null);

        // the announcement carried the lease: it ended by lease expiry, not by an operator
    }

    [Fact]
    public async Task Max_duration_watchdog_ends_a_live_announcement_at_start_plus_max()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await LiveTest.EnrollAsync(app, "rhs-lease2");
        LiveTest.SetSetting(app, "talk_lease_renew_interval_s", "600");
        LiveTest.SetSetting(app, "preparation_window_s", "1");

        var created = await app.Json(await app.Post("/api/announcements/lease", new { max_duration_s = 30 }));
        var live = await app.Json(await app.Post("/api/announcements/live", new { target = new { all = true } }));
        var annId = live.GetProperty("announcement_id").GetString()!;
        Assert.Equal(30, live.GetProperty("max_duration_s").GetInt32());

        var ended = await LiveTest.PollJsonAsync(app, $"/api/announcements/{annId}",
            j => j.GetProperty("status").GetString() == "ended", maxMs: 45_000);
        Assert.Equal("timeout", ended.GetProperty("ended_by").GetString());
    }

    [Fact]
    public async Task Role_gate_viewer_403_out_of_scope_operator_403_in_scope_operator_201()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var group = await app.Json(await app.Post("/api/admin/groups", new { name = "Ridge rooms" }));
        long groupId = group.GetProperty("id").GetInt64();
        var other = await app.Json(await app.Post("/api/admin/groups", new { name = "Gym" }));
        long otherId = other.GetProperty("id").GetInt64();

        await app.CreateUser("op-scope", LiveTest.Password, "operator", new List<long> { groupId }, schoolwide: false);
        await app.CreateUser("op-wide", LiveTest.Password, "operator", new List<long>(), schoolwide: true);
        await app.CreateUser("watcher", LiveTest.Password, "viewer", new List<long>(), schoolwide: false);

        // viewer: no lease, no live, no recorded — history only
        await app.LoginAs("watcher", LiveTest.Password);
        var viewerLease = await app.Post("/api/announcements/lease", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Forbidden, viewerLease.StatusCode);
        Assert.Contains("administrators and operators", (await app.Json(viewerLease)).GetProperty("detail").GetString()!);
        var viewerLive = await app.Post("/api/announcements/live", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Forbidden, viewerLive.StatusCode);
        var viewerHistory = await app.Get("/api/history");
        Assert.Equal(HttpStatusCode.OK, viewerHistory.StatusCode);

        // operator without schoolwide_broadcast: targeting all is refused, in-scope groups are fine
        await app.LoginAs("op-scope", LiveTest.Password);
        var outOfScope = await app.Post("/api/announcements/lease", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Forbidden, outOfScope.StatusCode);
        Assert.Contains("schoolwide_broadcast", (await app.Json(outOfScope)).GetProperty("detail").GetString()!);
        var outOfScopeLive = await app.Post("/api/announcements/live", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Forbidden, outOfScopeLive.StatusCode);
        var otherGroupLive = await app.Post("/api/announcements/live", new { target = new { groups = new[] { otherId } } });
        Assert.Equal(HttpStatusCode.Forbidden, otherGroupLive.StatusCode);   // still checked before any lease
        var inScope = await app.Post("/api/announcements/lease", new { target = new { groups = new[] { groupId } } });
        Assert.Equal(HttpStatusCode.Created, inScope.StatusCode);

        // operator with schoolwide_broadcast: all endpoints allowed
        await app.LoginAs("op-wide", LiveTest.Password);
        var conflict = await app.Post("/api/announcements/lease", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);          // op-scope holds the only lease
        var release = await app.Delete("/api/announcements/lease/" +
            (await app.Json(await app.Get("/api/announcements/lease/active"))).GetProperty("lease").GetProperty("lease_id").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, release.StatusCode);          // not the holder: 403, not 404
        await app.LoginAs("op-scope", LiveTest.Password);
        await app.Delete("/api/announcements/lease/" +
            (await app.Json(await app.Get("/api/announcements/lease/active"))).GetProperty("lease").GetProperty("lease_id").GetString());
        await app.LoginAs("op-wide", LiveTest.Password);
        var wide = await app.Post("/api/announcements/lease", new { target = new { all = true } });
        Assert.Equal(HttpStatusCode.Created, wide.StatusCode);
    }

    [Fact]
    public async Task Bodiless_post_to_the_lease_path_keeps_the_honest_501_and_points_at_the_json_api()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var res = await app.Post("/api/announcements/lease", null);       // exactly what the S1 stub test sent
        Assert.Equal((HttpStatusCode)501, res.StatusCode);
        Assert.Contains("S2", (await app.Json(res)).GetProperty("detail").GetString()!);
        var withBody = await app.Post("/api/announcements/lease", new { });
        Assert.Equal(HttpStatusCode.Created, withBody.StatusCode);
    }

    [Fact]
    public async Task Lease_holder_check_stops_another_operator_using_someone_elses_lease()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        await app.CreateUser("op-a", LiveTest.Password, "operator", new List<long>(), schoolwide: true);
        await app.CreateUser("op-b", LiveTest.Password, "operator", new List<long>(), schoolwide: true);

        await app.LoginAs("op-a", LiveTest.Password);
        var lease = await app.Json(await app.Post("/api/announcements/lease", new { max_duration_s = 120 }));
        var leaseId = lease.GetProperty("lease_id").GetString()!;

        await app.LoginAs("op-b", LiveTest.Password);
        var renew = await app.Post($"/api/announcements/lease/{leaseId}/renew", new { });
        Assert.Equal(HttpStatusCode.Forbidden, renew.StatusCode);
        Assert.Contains("lease held by op-a", (await app.Json(renew)).GetProperty("detail").GetString()!);
        var live = await app.Post("/api/announcements/live", new { target = new { all = true }, lease_id = leaseId });
        Assert.Equal(HttpStatusCode.Forbidden, live.StatusCode);
        var del = await app.Delete($"/api/announcements/lease/{leaseId}");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }
}

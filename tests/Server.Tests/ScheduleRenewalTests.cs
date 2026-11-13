using System.Text.Json;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

public class ScheduleRenewalTests
{
    static TemplateInput Regular(string start = "08:30") => new("Regular", new(true, null, null),
        [new("First", start, "09:20", 0, "std-bell", "announcement", 3)], [1, 2, 3, 4, 5]);

    static ScheduleManifestPayload Latest(TestEnv env)
    {
        using var c = env.Database.Open();
        return JsonSerializer.Deserialize<ScheduleManifestPayload>((string)c.ScalarForTests(
            "SELECT manifest_json FROM published_revisions ORDER BY revision DESC LIMIT 1")!, ControlEnvelope.Json)!;
    }

    [Fact]
    public void Renewal_extends_horizon_before_expiry_preserves_ids_and_ignores_drafts()
    {
        using var env = new TestEnv("America/Chicago", now: DateTimeOffset.Parse("2026-11-12T12:00:00Z"));
        env.Templates.Create(env.Admin(), Regular());
        env.Publish.Publish("admin");
        var original = Latest(env);
        env.Templates.Update(env.Admin(), 1, Regular("08:45"));
        env.Time.Now = env.Time.Now.AddDays(1);
        var result = env.Publish.RenewIfNeeded();
        Assert.Equal(200, result!.Status);
        var renewed = Latest(env);
        Assert.Equal(original.Revision + 1, renewed.Revision);
        Assert.True(renewed.HorizonEnd > original.HorizonEnd);
        Assert.All(renewed.Occurrences.Where(o => o.Kind == "start"), o => Assert.Equal("08:30", o.LocalHm));
        var overlap = original.Occurrences.First(o => o.LocalDate == "2026-11-13");
        Assert.Contains(renewed.Occurrences, o => o.OccurrenceId == overlap.OccurrenceId);
        Assert.True(ManifestSigner.Verify(renewed, ManifestSigner.DeriveDeviceVerifyKey(TrustPin.Get(env.Database))));
        Assert.Null(env.Publish.RenewIfNeeded()); // no revision churn within the day
    }

    [Fact]
    public void Renewal_recovers_after_server_outage_and_keeps_published_calendar()
    {
        using var env = new TestEnv("America/Chicago", now: DateTimeOffset.Parse("2026-11-06T12:00:00Z"));
        env.Templates.Create(env.Admin(), Regular());
        env.Templates.AddHoliday(env.Admin(), "2026-11-16", "Holiday");
        env.Templates.UpsertOverride(env.Admin(), "2026-11-17", null);
        env.Publish.Publish("admin");
        env.Templates.DeleteHoliday(env.Admin(), "2026-11-16");
        env.Templates.DeleteOverride(env.Admin(), "2026-11-17");
        env.Time.Now = env.Time.Now.AddDays(10);
        Assert.Equal(200, env.Publish.RenewIfNeeded()!.Status);
        var m = Latest(env);
        Assert.True(m.ExpiresAt > env.Time.Now.ToUnixTimeMilliseconds());
        Assert.DoesNotContain(m.Occurrences, o => o.LocalDate is "2026-11-16" or "2026-11-17");
    }

    [Fact]
    public void First_schedule_still_requires_explicit_publish()
    {
        using var env = new TestEnv("America/Chicago");
        env.Templates.Create(env.Admin(), Regular());
        Assert.Null(env.Publish.RenewIfNeeded());
        Assert.Equal(0L, env.Publish.LatestRevision());
    }

    [Fact]
    public void Legacy_schedule_adopts_matching_rules_but_refuses_unpublished_time_changes()
    {
        using var env = new TestEnv("America/Chicago", now: DateTimeOffset.Parse("2026-11-12T12:00:00Z"));
        env.Templates.Create(env.Admin(), Regular());
        env.Publish.Publish("admin");
        using (var c = env.Database.Open()) Db.Execute(c, "UPDATE published_revisions SET rules_json = NULL");
        env.Time.Now = env.Time.Now.AddDays(1);
        env.Templates.Update(env.Admin(), 1, Regular("08:45"));
        Assert.Equal(409, env.Publish.RenewIfNeeded()!.Status);
        Assert.Equal(1L, env.Publish.LatestRevision());
        env.Templates.Update(env.Admin(), 1, Regular());
        Assert.Equal(200, env.Publish.RenewIfNeeded()!.Status);
    }

    [Fact]
    public void Upgrade_captures_legacy_baseline_immediately_before_later_draft_edits()
    {
        using var env = new TestEnv("America/Chicago", now: DateTimeOffset.Parse("2026-11-12T12:00:00Z"));
        env.Templates.Create(env.Admin(), Regular());
        env.Publish.Publish("admin");
        using (var c = env.Database.Open()) Db.Execute(c, "UPDATE published_revisions SET rules_json = NULL");
        Assert.Null(env.Publish.RenewIfNeeded()); // same day: snapshot only, no extra revision
        env.Templates.Update(env.Admin(), 1, Regular("08:45"));
        env.Time.Now = env.Time.Now.AddDays(1);
        Assert.Equal(200, env.Publish.RenewIfNeeded()!.Status);
        Assert.All(Latest(env).Occurrences.Where(o => o.Kind == "start"), o => Assert.Equal("08:30", o.LocalHm));
    }

    [Fact]
    public void Renewal_runs_on_school_midnight_across_dst_and_concurrent_checks_publish_once()
    {
        using var env = new TestEnv("America/Chicago", now: DateTimeOffset.Parse("2026-10-31T12:00:00Z"));
        env.Templates.Create(env.Admin(), Regular());
        env.Publish.Publish("admin");
        var previousEnd = Latest(env).HorizonEnd;
        env.Time.Now = DateTimeOffset.Parse("2026-11-01T04:59:00Z"); // still Saturday in Chicago
        Assert.Null(env.Publish.RenewIfNeeded());
        env.Time.Now = DateTimeOffset.Parse("2026-11-01T05:01:00Z");
        Parallel.For(0, 5, _ => env.Publish.RenewIfNeeded());
        Assert.Equal(2L, env.Publish.LatestRevision());
        Assert.Equal(86_400_000, Latest(env).HorizonEnd - previousEnd);
        // The new horizon crosses the fall-back day; all local starts remain 08:30.
        Assert.All(Latest(env).Occurrences.Where(o => o.Kind == "start"), o => Assert.Equal("08:30", o.LocalHm));
    }

    [Fact]
    public async Task Published_sound_remains_downloadable_after_draft_audio_replacement()
    {
        using var app = new TestApp("America/Chicago");
        await app.LoginAsAdmin();
        var publish = app.Factory.Services.GetRequiredService<PublishService>();
        Assert.Equal(200, publish.Publish("admin").Status);
        var sounds = app.Factory.Services.GetRequiredService<SoundService>();
        var original = sounds.Get("std-bell")!;
        var url = $"/api/sounds/std-bell?sha={original.Sha256}";
        var before = await (await app.Get(url)).Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, (await app.Upload("/api/sounds/std-bell", "file", "changed.wav",
            LiveTest.BuildWav(16000, 16000, value: 1234), method: "PUT")).StatusCode);
        Assert.NotEqual(original.Sha256, sounds.Get("std-bell")!.Sha256);
        var frozen = await app.Get(url);
        Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        Assert.Equal(before, await frozen.Content.ReadAsByteArrayAsync());
    }
}

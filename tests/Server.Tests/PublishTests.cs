using SchoolBell.Server;
using System.Text.Json;
using NodaTime;
using SchoolBell.Contracts;

namespace Server.Tests;

public class ScheduleCompilerTests
{
    static readonly DateTimeZone Chicago = DateTimeZoneProviders.Tzdb["America/Chicago"];

    static CompileTemplate Template(long id, string name, string startHm, string endHm, int warn, int[] weekdays) =>
        new(id, name, new TargetScope(true, null, null),
            [new CompilePeriod(0, "Period 1", startHm, endHm, warn, "std-bell", "announcement", 3)],
            new SortedSet<int>(weekdays));

    [Fact]
    public void Normal_week_compiles_start_end_and_warn_per_period()
    {
        // Wed 2026-11-04 .. Tue 2026-11-10; Mon-Fri assignment -> Nov 4,5,6,9,10 = 5 days x 3 = 15
        var res = ScheduleCompiler.Compile(Chicago,
            new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 10),
            [Template(1, "Regular", "08:30", "09:20", 5, [1, 2, 3, 4, 5])],
            [], [], 0);
        Assert.Equal(15, res.Occurrences.Count);
        Assert.Equal(5, res.Occurrences.Count(o => o.Kind == "warn"));
        Assert.Equal(5, res.Occurrences.Count(o => o.Kind == "start"));
        Assert.Equal(5, res.Occurrences.Count(o => o.Kind == "end"));
        // Nov 4 2026 is CST (UTC-6): 08:30 local == 14:30Z
        var start = res.Occurrences.Single(o => o.Kind == "start" && o.LocalDate == "2026-11-04");
        Assert.Equal(Instant.FromUtc(2026, 11, 4, 14, 30).ToUnixTimeMilliseconds(), start.StartsAt);
    }

    static CompilePeriod P(int idx, string name, string start, string end, string sound = "std-bell",
        string policy = "announcement", int seconds = 3) => new(idx, name, start, end, 0, sound, policy, seconds);

    [Fact]
    public void Back_to_back_periods_ring_once_at_the_changeover()
    {
        // 3rd block ends 13:21 as pass starts; pass ends 13:24 as 4th block starts
        var hs = new CompileTemplate(1, "High School", new TargetScope(true, null, null),
            [P(0, "3rd block", "11:09", "13:21"), P(1, "pass", "13:21", "13:24"), P(2, "4th block", "13:24", "15:03")],
            new SortedSet<int>([3]));
        var res = ScheduleCompiler.Compile(Chicago, new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 4), [hs], [], [], 0);

        Assert.Equal(["11:09", "13:21", "13:24", "15:03"], res.Occurrences.OrderBy(o => o.StartsAt).Select(o => o.LocalHm));
        var changeover = res.Occurrences.Single(o => o.LocalHm == "13:21");
        Assert.Equal("start", changeover.Kind);                     // the starting period represents the bell
        Assert.Equal("pass / 3rd block", changeover.BellName);
        Assert.Equal(OccurrenceIds.Compute(1, 1, OccurrenceIds.TargetKey(hs.Target), "2026-11-04", "start"),
            changeover.OccurrenceId);                              // stable id of the start bell
        Assert.Equal("end", res.Occurrences.Single(o => o.LocalHm == "15:03").Kind);  // a lone end bell still rings
    }

    [Fact]
    public void Two_schedules_with_the_same_time_and_target_ring_once()
    {
        var all = new TargetScope(true, null, null);
        var hs = new CompileTemplate(1, "High School", all, [P(0, "Teacher Sign in", "07:15", "07:20")], new SortedSet<int>([3]));
        var jh = new CompileTemplate(2, "JH Bell", all,
            [P(0, "Teacher sign in", "07:15", "07:20", sound: "std-chime", policy: "inherit", seconds: 5)], new SortedSet<int>([3]));
        var res = ScheduleCompiler.Compile(Chicago, new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 4), [hs, jh], [], [], 0);

        Assert.Equal(2, res.Occurrences.Count);                    // 07:15 and 07:20, once each
        var first = res.Occurrences.Single(o => o.LocalHm == "07:15");
        Assert.Equal("Teacher Sign in", first.BellName);           // same name in other spelling: shown once
        Assert.Equal("std-bell", first.SoundId);                   // first schedule's bell
        Assert.Equal("announcement", first.VolumePolicy);          // the louder policy wins
        Assert.Equal(5, first.DurationS);                          // the longer bell wins
        Assert.Equal("High School / JH Bell", first.ScheduleTemplate);
    }

    [Fact]
    public void Same_time_for_different_targets_is_not_merged_by_the_server()
    {
        var hs = new CompileTemplate(1, "High School", new TargetScope(true, null, null),
            [P(0, "Lunch", "11:30", "12:00")], new SortedSet<int>([3]));
        var jh = new CompileTemplate(2, "JH", new TargetScope(false, [7], null),
            [P(0, "Lunch", "11:30", "12:00")], new SortedSet<int>([3]));
        var res = ScheduleCompiler.Compile(Chicago, new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 4), [hs, jh], [], [], 0);
        Assert.Equal(4, res.Occurrences.Count);                    // each computer merges what reaches it
    }

    [Fact]
    public void Holiday_wins_over_override_and_weekday()
    {
        var res = ScheduleCompiler.Compile(Chicago,
            new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 4),
            [Template(1, "Regular", "08:30", "09:20", 0, [2])],
            new() { ["2026-11-04"] = 1L },      // override says: use template 1
            new() { ["2026-11-04"] = "Veterans Day" }, // holiday wins
            0);
        Assert.Empty(res.Occurrences);
        Assert.Contains("2026-11-04", res.Report.HolidayDates);
    }

    [Fact]
    public void Null_override_means_no_bells_day()
    {
        var res = ScheduleCompiler.Compile(Chicago,
            new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 4),
            [Template(1, "Regular", "08:30", "09:20", 0, [2])],
            new() { ["2026-11-04"] = null },
            [], 0);
        Assert.Empty(res.Occurrences);
        Assert.Contains("2026-11-04", res.Report.NoBellDates);
    }

    [Fact]
    public void Override_switches_the_day_to_another_template()
    {
        var regular = Template(1, "Regular", "08:30", "09:20", 0, [2]);
        var exam = new CompileTemplate(2, "Exam", new TargetScope(true, null, null),
            [new CompilePeriod(0, "Exam start", "10:00", "12:00", 0, "std-chime", "inherit", 5)],
            new SortedSet<int>());
        var res = ScheduleCompiler.Compile(Chicago,
            new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 4),
            [regular, exam],
            new() { ["2026-11-04"] = 2L }, [], 0);
        Assert.Equal(2, res.Occurrences.Count); // exam start+end only
        Assert.All(res.Occurrences, o => Assert.Equal("Exam", o.ScheduleTemplate));
    }

    [Fact]
    public void Dst_spring_forward_nonexistent_local_time_is_skipped()
    {
        // 2026-03-08 America/Chicago: 02:30 local does not exist (02:00 -> 03:00)
        var res = ScheduleCompiler.Compile(Chicago,
            new LocalDate(2026, 3, 8), new LocalDate(2026, 3, 8),
            [Template(1, "Ghost bell", "02:30", "02:45", 0, [0])], [], [], 0);
        Assert.Empty(res.Occurrences);
        Assert.Equal(2, res.Report.SkippedDst.Count); // start and end both nonexistent
        Assert.All(res.Report.SkippedDst, s =>
        {
            Assert.Equal("dst_nonexistent", s.Reason);
            Assert.Equal("2026-03-08", s.LocalDate);
        });
    }

    [Fact]
    public void Dst_fall_back_ambiguous_local_time_runs_once_at_first_mapping()
    {
        // 2026-11-01 America/Chicago: 01:30 is ambiguous (CDT then CST). First mapping = CDT = 06:30Z.
        var res = ScheduleCompiler.Compile(Chicago,
            new LocalDate(2026, 11, 1), new LocalDate(2026, 11, 1),
            [Template(1, "Ambiguous", "01:30", "01:45", 0, [0])], [], [], 0);
        Assert.Equal(2, res.Occurrences.Count);
        var start = res.Occurrences.Single(o => o.Kind == "start");
        Assert.Equal(Instant.FromUtc(2026, 11, 1, 6, 30).ToUnixTimeMilliseconds(), start.StartsAt);
        Assert.Equal(2, res.Report.AmbiguousFirstMapping.Count);
    }

    [Fact]
    public void Unambiguous_times_are_not_flagged()
    {
        var res = ScheduleCompiler.Compile(Chicago,
            new LocalDate(2026, 11, 4), new LocalDate(2026, 11, 4),
            [Template(1, "Regular", "08:30", "09:20", 5, [2])], [], [], 0);
        Assert.Empty(res.Report.SkippedDst);
        Assert.Empty(res.Report.AmbiguousFirstMapping);
    }
}

public class PublishTests
{
    static readonly DateTimeOffset Nov4 = DateTimeOffset.Parse("2026-11-04T13:00:00Z"); // Wed 08:00 CST

    static TestEnv Env(DateTimeOffset? now = null)
    {
        var env = new TestEnv(now: now ?? Nov4);
        env.SetTz("America/Chicago");
        return env;
    }

    static TemplateInput Regular(string startHm = "08:30", string endHm = "09:20") =>
        new("Regular Day", new TargetScope(true, null, null),
            [new PeriodInput("Period 1", startHm, endHm, 5, "std-bell", "announcement", 3)],
            [1, 2, 3, 4, 5]);

    [Fact]
    public void Publish_requires_timezone()
    {
        using var env = new TestEnv(); // tz empty
        var res = env.Publish.Publish("admin");
        Assert.Equal(409, res.Status);
        Assert.Equal("timezone not configured", res.Detail);
    }

    [Fact]
    public void Publish_compiles_next_seven_days_and_stores_revision()
    {
        using var env = Env();
        Assert.Equal(201, env.Templates.Create(env.Admin(), Regular()).Status);
        var res = env.Publish.Publish("admin");
        Assert.Equal(200, res.Status);
        Assert.NotNull(res.Result);
        // Nov 4,5,6 (Wed-Fri) + Nov 9,10 (Mon-Tue) => 5 school days x 3 = 15
        Assert.Equal(15, res.Result!.OccurrenceCount);
        Assert.Equal(1, res.Result.Revision);
        Assert.True(res.Result.ExpiresAt > res.Result.HorizonEnd); // +1 h grace

        using var conn = env.Database.Open();
        Assert.Equal(15L, Convert.ToInt64(conn.ScalarForTests(
            "SELECT COUNT(*) FROM occurrences WHERE revision = 1")));
    }

    [Fact]
    public void Manifest_signature_verifies_with_device_key_derived_from_trust_pin()
    {
        using var env = Env();
        env.Templates.Create(env.Admin(), Regular());
        Assert.Equal(200, env.Publish.Publish("admin").Status);

        using var conn = env.Database.Open();
        var json = conn.ScalarForTests("SELECT manifest_json FROM published_revisions WHERE revision = 1") as string;
        var manifest = JsonSerializer.Deserialize<ScheduleManifestPayload>(json!, ControlEnvelope.Json)!;
        var pin = TrustPin.Get(env.Database);
        var deviceKey = ManifestSigner.DeriveDeviceVerifyKey(pin);
        Assert.True(ManifestSigner.Verify(manifest, deviceKey));

        // tampering fails
        var tampered = JsonSerializer.Deserialize<ScheduleManifestPayload>(json!, ControlEnvelope.Json)!;
        tampered.Occurrences[0] = manifest.Occurrences[0] with { StartsAt = manifest.Occurrences[0].StartsAt + 60_000 };
        Assert.False(ManifestSigner.Verify(tampered, deviceKey));
    }

    [Fact]
    public void Manifest_lists_seeded_sounds_and_horizon_fields()
    {
        using var env = Env();
        env.Templates.Create(env.Admin(), Regular());
        env.Publish.Publish("admin");
        using var conn = env.Database.Open();
        var json = conn.ScalarForTests("SELECT manifest_json FROM published_revisions") as string;
        var manifest = JsonSerializer.Deserialize<ScheduleManifestPayload>(json!, ControlEnvelope.Json)!;
        Assert.Equal(3, manifest.Sounds.Count); // std-bell/std-chime/std-buzzer
        Assert.Contains(manifest.Sounds, s => s.Id == "std-bell" && s.Url == $"/api/sounds/std-bell?sha={s.Sha256}" && s.Sha256.Length == 64);
        Assert.Equal("America/Chicago", manifest.SchoolTz);
        // Nov 4 midnight CST == 06:00Z
        Assert.Equal(Instant.FromUtc(2026, 11, 4, 6, 0).ToUnixTimeMilliseconds(), manifest.HorizonStart);
    }

    [Fact]
    public void Occurrence_ids_are_stable_across_time_edits()
    {
        using var env = Env();
        env.Templates.Create(env.Admin(), Regular());
        Assert.Equal(200, env.Publish.Publish("admin").Status);
        var ids1 = OccurrenceIdsAt(env, 1);
        var starts1 = OccurrenceStarts(env, 1);

        // edit the time (keep names/periods): republish
        var edited = Regular(startHm: "08:35", endHm: "09:25");
        Assert.Equal(200, env.Templates.Update(env.Admin(), 1, edited).Status);
        Assert.Equal(200, env.Publish.Publish("admin").Status);
        var ids2 = OccurrenceIdsAt(env, 2);
        var starts2 = OccurrenceStarts(env, 2);

        Assert.Equal(ids1, ids2);                 // same stable ids
        Assert.NotEqual(starts1, starts2);        // but moved starts_at values
        // each day's start bell moved by exactly 5 minutes
        var diff = starts2.Zip(starts1, (a, b) => a - b).ToList();
        Assert.All(diff, d => Assert.IsType<long>(d));
        Assert.Equal(300_000, diff.First(d => d != 0));
    }

    [Fact]
    public void Override_and_holiday_change_compiled_occurrences()
    {
        using var env = Env();
        env.Templates.Create(env.Admin(), Regular());
        env.Templates.UpsertOverride(env.Admin(), "2026-11-05", null);   // Thursday: no bells
        env.Templates.AddHoliday(env.Admin(), "2026-11-06", "Veterans Day observed"); // Friday: holiday
        var res = env.Publish.Publish("admin");
        Assert.Equal(200, res.Status);
        Assert.Equal(9, res.Result!.OccurrenceCount); // 3 days x 3
        using var conn = env.Database.Open();
        Assert.Equal(0L, Convert.ToInt64(conn.ScalarForTests(
            "SELECT COUNT(*) FROM occurrences WHERE revision=1 AND local_date='2026-11-05'")));
        Assert.Equal(0L, Convert.ToInt64(conn.ScalarForTests(
            "SELECT COUNT(*) FROM occurrences WHERE revision=1 AND local_date='2026-11-06'")));
    }

    [Fact]
    public void Publish_enqueues_manifest_to_every_non_revoked_device_outbox()
    {
        using var env = Env();
        env.Templates.Create(env.Admin(), Regular());
        var t1 = env.Enrollment.CreateToken("admin", "t", 24, 3, []);
        var d1 = env.Enrollment.Enroll(t1.Token!, "dev-one").Result!;
        var d2 = env.Enrollment.Enroll(t1.Token!, "dev-two").Result!;
        var d3 = env.Enrollment.Enroll(t1.Token!, "dev-three").Result!;
        env.Enrollment.RevokeDevice("admin", d3.DeviceId);

        env.Publish.Publish("admin");

        Assert.Equal(1L, ManifestEventsFor(env, d1.DeviceId));
        Assert.Equal(1L, ManifestEventsFor(env, d2.DeviceId));
        Assert.Equal(0L, ManifestEventsFor(env, d3.DeviceId)); // revoked device gets nothing
    }

    static long ManifestEventsFor(TestEnv env, string deviceId)
    {
        using var conn = env.Database.Open();
        return conn.ScalarsForTests(
            "SELECT COUNT(*) FROM outbox WHERE device_id = $d AND event_json LIKE $pat",
            ("$d", deviceId), ("$pat", "%schedule.manifest%")).Single();
    }

    [Fact]
    public void Dst_publish_anchor_reports_skipped_and_first_mapping()
    {
        // Spring forward week: publish on Mar 6 2026 (Friday), horizon covers Mar 8 (Sunday gap).
        using var spring = new TestEnv(now: DateTimeOffset.Parse("2026-03-06T13:00:00Z"));
        spring.SetTz("America/Chicago");
        var ghost = new TemplateInput("Sunday gap", new TargetScope(true, null, null),
            [new PeriodInput("Gap bell", "02:30", "03:30", 0, "std-bell", "announcement", 3)], [0]);
        Assert.Equal(201, spring.Templates.Create(spring.Admin(), ghost).Status);
        var r1 = spring.Publish.Publish("admin");
        Assert.Equal(200, r1.Status);
        // 02:30 nonexistent -> skipped; 03:30 start of end-hm compiles (03:30 exists post-gap)
        Assert.Contains(r1.Result!.Report.SkippedDst, s => s.LocalDate == "2026-03-08" && s.LocalHm == "02:30");
        Assert.DoesNotContain(r1.Result.Report.SkippedDst, s => s.LocalHm == "03:30");
        using (var conn = spring.Database.Open())
            Assert.Equal(0L, Convert.ToInt64(conn.ScalarForTests(
                "SELECT COUNT(*) FROM occurrences WHERE local_date='2026-03-08' AND starts_at >= 0 AND local_hm='02:30'")));

        // Fall back week: publish Oct 30 2026, horizon covers Nov 1 01:30 (ambiguous, runs once at CDT)
        using var fall = new TestEnv(now: DateTimeOffset.Parse("2026-10-30T13:00:00Z"));
        fall.SetTz("America/Chicago");
        var amb = new TemplateInput("Sunday amb", new TargetScope(true, null, null),
            [new PeriodInput("Amb bell", "01:30", "01:45", 0, "std-bell", "announcement", 3)], [0]);
        Assert.Equal(201, fall.Templates.Create(fall.Admin(), amb).Status);
        var r2 = fall.Publish.Publish("admin");
        using var conn2 = fall.Database.Open();
        var starts = conn2.ScalarsForTests(
            "SELECT starts_at FROM occurrences WHERE local_date='2026-11-01' AND kind='start'");
        Assert.Single(starts); // exactly once
        Assert.Equal(Instant.FromUtc(2026, 11, 1, 6, 30).ToUnixTimeMilliseconds(), starts[0]); // first mapping (CDT)
        Assert.Contains(r2.Result!.Report.AmbiguousFirstMapping, s => s.LocalDate == "2026-11-01");
    }

    static List<string> OccurrenceIdsAt(TestEnv env, long revision)
    {
        using var conn = env.Database.Open();
        return conn.ScalarsForStrings(
            "SELECT occurrence_id FROM occurrences WHERE revision = $r ORDER BY occurrence_id",
            ("$r", revision));
    }

    static List<long> OccurrenceStarts(TestEnv env, long revision)
    {
        using var conn = env.Database.Open();
        return conn.ScalarsForTests(
            "SELECT starts_at FROM occurrences WHERE revision = $r ORDER BY local_date, starts_at",
            ("$r", revision));
    }
}

internal static class QueryExt
{
    public static List<long> ScalarsForTests(this Microsoft.Data.Sqlite.SqliteConnection conn, string sql,
        params (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    public static List<string> ScalarsForStrings(this Microsoft.Data.Sqlite.SqliteConnection conn, string sql,
        params (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}

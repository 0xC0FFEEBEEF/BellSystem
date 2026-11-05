using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class BellSchedulerTests
{
    private const long T0 = 1_793_700_000_000; // arbitrary Unix ms

    private static (BellScheduler sched, ClaimLedger ledger, string dir) NewSched(int grace = 2)
    {
        var dir = TestDirs.Create("sched");
        return (new BellScheduler(new ClaimLedger(dir), grace), new ClaimLedger(dir), dir);
    }

    [Fact]
    public void Due_plus_1s_within_grace_plays()
    {
        var dir = TestDirs.Create("sched-play");
        try
        {
            var ledger = new ClaimLedger(dir);
            var sched = new BellScheduler(ledger, graceSeconds: 2);
            var m = ManifestTestKit.BuildManifest(1, T0,
                [ManifestTestKit.Bell("b1", startsAt: T0 - 1_000, expiresAt: T0 + 100_000)]);

            var ev = sched.Evaluate(m, T0, liveActive: false, "dev", []);
            Assert.Single(ev.Decisions);
            Assert.Equal(BellAction.Play, ev.Decisions[0].Action);
            Assert.True(ledger.Has("b1")); // claimed BEFORE playback instruction
            Assert.False(ev.StaleCache);

            // Duplicate trigger does nothing.
            var again = sched.Evaluate(m, T0 + 100, liveActive: false, "dev", []);
            Assert.Empty(again.Decisions);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Due_plus_3s_beyond_grace_expires_never_plays_late()
    {
        var dir = TestDirs.Create("sched-grace");
        try
        {
            var ledger = new ClaimLedger(dir);
            var sched = new BellScheduler(ledger, graceSeconds: 2);
            var m = ManifestTestKit.BuildManifest(1, T0,
                [ManifestTestKit.Bell("late", startsAt: T0 - 3_000, expiresAt: T0 + 100_000)]);

            var ev = sched.Evaluate(m, T0, liveActive: false, "dev", []);
            Assert.Single(ev.Decisions);
            Assert.Equal(BellAction.Expired, ev.Decisions[0].Action);
            Assert.True(ledger.Has("late")); // recorded, not replayed
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Bells_already_past_when_the_schedule_was_published_are_not_reported_missed()
    {
        var dir = TestDirs.Create("sched-before-publish");
        try
        {
            var ledger = new ClaimLedger(dir);
            var sched = new BellScheduler(ledger, graceSeconds: 2);
            // published at 15:05 with today's 15:03 bell still in it (the horizon starts at midnight)
            var m = ManifestTestKit.BuildManifest(2, T0, [
                ManifestTestKit.Bell("before-publish", startsAt: T0 - 120_000, expiresAt: T0 + 100_000),
                ManifestTestKit.Bell("after-publish", startsAt: T0 - 30_000, expiresAt: T0 + 100_000),
            ], generatedAt: T0 - 60_000);

            var ev = sched.Evaluate(m, T0, liveActive: false, "dev", []);
            var d = Assert.Single(ev.Decisions);                     // only the bell this device missed
            Assert.Equal("after-publish", d.OccurrenceId);
            Assert.Equal(BellAction.Expired, d.Action);
            Assert.False(ledger.Has("before-publish"));
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Past_horizon_after_sleep_yields_exactly_N_expired_in_one_batch()
    {
        var dir = TestDirs.Create("sched-horizon");
        try
        {
            var ledger = new ClaimLedger(dir);
            var sched = new BellScheduler(ledger, graceSeconds: 2);
            // A whole morning of bells, all far in the past (long sleep/reboot).
            var bells = Enumerable.Range(0, 9)
                .Select(i => ManifestTestKit.Bell("h" + i, startsAt: T0 - (60_000 + i * 3_600_000L), expiresAt: T0 + 100_000))
                .ToList();
            var morning = ManifestTestKit.BuildManifest(1, T0, bells);

            var ev = sched.Evaluate(morning, T0, liveActive: false, "dev", []);
            Assert.Equal(9, ev.Decisions.Count); // exactly N
            Assert.All(ev.Decisions, d => Assert.Equal(BellAction.Expired, d.Action));
            Assert.Null(ev.NextDueMs);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Due_bell_during_live_announcement_is_suppressed_and_never_late()
    {
        var dir = TestDirs.Create("sched-suppress");
        try
        {
            var ledger = new ClaimLedger(dir);
            var sched = new BellScheduler(ledger, graceSeconds: 2);
            var m = ManifestTestKit.BuildManifest(1, T0,
                [ManifestTestKit.Bell("live-collide", startsAt: T0 - 500, expiresAt: T0 + 100_000)]);

            var ev = sched.Evaluate(m, T0, liveActive: true, "dev", []);
            Assert.Single(ev.Decisions);
            Assert.Equal(BellAction.Suppressed, ev.Decisions[0].Action);

            // After the announcement ends, the same occurrence must NOT ring later.
            var after = ManifestTestKit.BuildManifest(2, T0,
                [ManifestTestKit.Bell("live-collide", startsAt: T0 - 500, expiresAt: T0 + 100_000)]);
            var ev2 = sched.Evaluate(after, T0 + 30_000, liveActive: false, "dev", []);
            Assert.Empty(ev2.Decisions); // claim ledger already covers it
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Stale_cache_stops_scheduling()
    {
        var dir = TestDirs.Create("sched-stale");
        try
        {
            var ledger = new ClaimLedger(dir);
            var sched = new BellScheduler(ledger, graceSeconds: 2);
            var m = ManifestTestKit.BuildManifest(1, T0,
                [ManifestTestKit.Bell("s", startsAt: T0 - 1_000, expiresAt: T0 + 100_000)]);

            var future = m.ExpiresAt + 10_000; // past expires_at
            var ev = sched.Evaluate(m, future, liveActive: false, "dev", []);
            Assert.True(ev.StaleCache);
            Assert.Empty(ev.Decisions);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Next_due_is_the_earliest_future_and_target_scope_filters()
    {
        var dir = TestDirs.Create("sched-next");
        try
        {
            var ledger = new ClaimLedger(dir);
            var sched = new BellScheduler(ledger, graceSeconds: 2);
            var forMe = ManifestTestKit.Bell("mine", T0 + 5_000, T0 + 100_000);
            var notForMe = ManifestTestKit.Bell("other", T0 + 1_000, T0 + 100_000,
                target: new TargetScope(All: false, null, ["someone-else"]));
            var m = ManifestTestKit.BuildManifest(1, T0, [notForMe, forMe]);

            var ev = sched.Evaluate(m, T0, liveActive: false, "dev", []);
            Assert.Equal(T0 + 5_000, ev.NextDueMs); // filtered: the other device's bell is not ours
            Assert.Empty(ev.Decisions);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Target_scope_matching_via_contracts()
    {
        var all = new TargetScope(All: true, null, null);
        var groups = new TargetScope(All: false, [3, 7], null);
        var devices = new TargetScope(All: false, null, ["dev-1", "dev-2"]);
        var none = new TargetScope(null, null, null);

        Assert.True(all.Matches("any", []));
        Assert.True(groups.Matches("dev", [7]));
        Assert.False(groups.Matches("dev", [4]));
        Assert.True(devices.Matches("dev-2", []));
        Assert.False(devices.Matches("dev-3", []));
        Assert.False(none.Matches("dev", [3]));
    }
}

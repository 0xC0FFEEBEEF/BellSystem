using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class BellClockTests
{
    [Fact]
    public void Sync_captures_offset_and_monotonic_elapsed_drives_now()
    {
        var time = new FakeTimeProvider(wallMs: 1_000_000, stampMs: 10_000);
        var clock = new BellClock(time);

        // auth.ok says the server is at 5_000_000 while local wall is 1_000_000 → offset +4_000_000.
        clock.Sync(serverTimeMs: 5_000_000);
        Assert.Equal(4_000_000, clock.OffsetMs());

        // 2_000 ms of monotonic elapsed (wall clock lied meanwhile — we never re-resolve it).
        time.AdvanceStamp(2_000);
        time.AdvanceWall(999_999);
        Assert.Equal(5_002_000, clock.NowMs());

        time.AdvanceStamp(10_000);
        Assert.Equal(5_012_000, clock.NowMs());
        Assert.True(clock.Synced);
    }

    [Fact]
    public void Re_sync_on_each_auth_ok_rebases_but_never_mid_wait_mid_elapsed()
    {
        var time = new FakeTimeProvider(wallMs: 100, stampMs: 1_000);
        var clock = new BellClock(time);
        clock.Sync(10_000);          // offset +9_900
        time.AdvanceStamp(500);
        Assert.Equal(10_500, clock.NowMs());

        clock.Sync(20_000);          // new connection: rebase at current stamp
        Assert.Equal(20_000, clock.NowMs());
        time.AdvanceStamp(1_000);
        Assert.Equal(21_000, clock.NowMs());
        Assert.Equal(20_000 + 1_000 - time.GetUtcNow().ToUnixTimeMilliseconds(), clock.OffsetMs());
    }

    [Fact]
    public void Unsynced_clock_uses_wall_offset_zero()
    {
        var time = new FakeTimeProvider(wallMs: 777, stampMs: 1);
        var clock = new BellClock(time);
        Assert.False(clock.Synced);
        Assert.Equal(0, clock.OffsetMs());
    }
}

public class KeepAwakePolicyStoreTests
{
    [Fact]
    public void Policy_active_before_expiry_inactive_after_without_any_server_contact()
    {
        var dir = TestDirs.Create("keepawake");
        try
        {
            var store = new KeepAwakePolicyStore(dir);
            store.Set("p1", expiresAt: 1_000_000, displayOff: false);

            var early = store.Evaluate(nowMs: 999_999);
            Assert.True(early.Desired);
            Assert.Equal(1_000_000, early.MaxExpiresAt);

            // Offline expiry release: pure local evaluation, no server involved.
            var late = store.Evaluate(nowMs: 1_000_000);
            Assert.False(late.Desired);
            Assert.Empty(store.All()); // expired policy durably dropped

            var reloaded = new KeepAwakePolicyStore(dir);
            Assert.False(reloaded.Evaluate(1_000_001).Desired);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Set_clear_and_display_off_merge_across_policies_and_survive_reload()
    {
        var dir = TestDirs.Create("keepawake2");
        try
        {
            var store = new KeepAwakePolicyStore(dir);
            store.Set("a", 100, displayOff: false);
            store.Set("b", 200, displayOff: true);
            var state = store.Evaluate(50);
            Assert.True(state.Desired);
            Assert.True(state.DisplayOff);       // any policy wanting display-off wins
            Assert.Equal(200, state.MaxExpiresAt);

            Assert.True(store.Clear("b"));
            Assert.False(store.Clear("b"));      // idempotent
            state = store.Evaluate(50);
            Assert.True(state.Desired);
            Assert.False(state.DisplayOff);

            var reloaded = new KeepAwakePolicyStore(dir);
            Assert.True(reloaded.Evaluate(50).Desired);
            Assert.Equal(100, reloaded.Evaluate(50).MaxExpiresAt);
        }
        finally { TestDirs.TryDelete(dir); }
    }
}

public class ReconnectPolicyTests
{
    [Fact]
    public void Full_jitter_delays_are_within_bounds_and_monotone_in_attempt_ceiling()
    {
        var rng = new Random(12345);
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), rng);

        for (int attempt = 1; attempt <= 12; attempt++)
        {
            var delay = policy.NextDelay();
            Assert.True(delay >= TimeSpan.Zero, $"attempt {attempt}: {delay} < 0");
            Assert.True(delay.TotalMilliseconds <= policy.CeilingMsForAttempt(attempt) + 1e-9,
                $"attempt {attempt}: {delay} exceeds ceiling {policy.CeilingMsForAttempt(attempt)}");
            Assert.True(delay.TotalMilliseconds <= 30_000, "cap 30 s honoured");
        }

        // Ceilings grow exponentially until they hit the cap, then stay at the cap.
        Assert.True(policy.CeilingMsForAttempt(5) < policy.CeilingMsForAttempt(6));
        Assert.Equal(30_000, policy.CeilingMsForAttempt(6));
        Assert.Equal(30_000, policy.CeilingMsForAttempt(20));
        Assert.Equal(12, policy.Attempt);
    }

    [Fact]
    public void Jitter_reaches_low_values_full_jitter_not_constant_ceiling()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), new Random(7));
        var delays = Enumerable.Range(0, 200).Select(_ => policy.NextDelay().TotalMilliseconds).ToList();
        Assert.True(delays.Min() < 1_000, "full jitter should sample near zero");
        Assert.True(delays.Average() > 100, "jitter should spread, not collapse to zero");
    }

    [Fact]
    public void Stable_connection_at_least_60s_resets_attempt_growth()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), new Random(1));
        for (int i = 0; i < 8; i++) policy.NextDelay();
        Assert.Equal(8, policy.Attempt);

        policy.ReportStableConnection(59_000);  // too short: no reset
        Assert.Equal(8, policy.Attempt);
        policy.ReportStableConnection(60_001);  // stable ≥60 s: reset
        Assert.Equal(0, policy.Attempt);
        Assert.True(policy.NextDelay().TotalMilliseconds <= 1_000 + 1e-9); // attempt 1 ceiling = base
    }
}

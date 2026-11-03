using SchoolBell.Server;
using SchoolBell.Contracts;

namespace Server.Tests;

public class KeepAwakeTests
{
    static (TestEnv env, string d1, string d2) TwoDevices()
    {
        var env = new TestEnv();
        var t = env.Enrollment.CreateToken("admin", "ka", 24, 2, []);
        var d1 = env.Enrollment.Enroll(t.Token!, "ka-one").Result!.DeviceId;
        var d2 = env.Enrollment.Enroll(t.Token!, "ka-two").Result!.DeviceId;
        return (env, d1, d2);
    }

    [Fact]
    public void Create_all_scope_enqueues_set_events_for_every_device()
    {
        var (env, d1, d2) = TwoDevices();
        var res = env.KeepAwake.Create("admin", "all", null, 60, null, false);
        Assert.Equal(201, res.Status);

        var view = env.KeepAwake.List().Single();
        Assert.Equal(2, view.DesiredCount);
        Assert.Equal(0, view.AckCount);
        Assert.False(view.Expired);

        using var conn = env.Database.Open();
        Assert.Equal(1L, SetEvents(conn, d1));
        Assert.Equal(1L, SetEvents(conn, d2));

        // ack arrives through the control channel path
        var policyId = view.Policy.PolicyId;
        env.KeepAwake.RecordAck(d1, new KeepAwakeAckPayload(policyId, env.Time.GetUtcNow().ToUnixTimeMilliseconds(), true));
        Assert.Equal(1, env.KeepAwake.List().Single().AckCount);
    }

    [Fact]
    public void Release_sends_clear_and_blocks_double_release()
    {
        var (env, d1, _) = TwoDevices();
        var policyId = Guid.NewGuid();
        Assert.Equal(201, env.KeepAwake.Create("admin", "device", d1, 30, null, false).Status);
        var created = env.KeepAwake.List().Single();
        Assert.Equal(200, env.KeepAwake.Release("admin", created.Policy.PolicyId).Status);
        using (var conn = env.Database.Open())
            Assert.Equal(1L, ClearEvents(conn, d1));
        Assert.Equal(409, env.KeepAwake.Release("admin", created.Policy.PolicyId).Status);
    }

    [Fact]
    public void Policies_have_absolute_expiry_and_expired_ones_are_never_resent()
    {
        var (env, d1, _) = TwoDevices();
        Assert.Equal(400, env.KeepAwake.Create("admin", "all", null, null,
            env.Time.GetUtcNow().ToUnixTimeMilliseconds() - 1000, false).Status); // past expiry

        Assert.Equal(201, env.KeepAwake.Create("admin", "all", null, 10, null, false).Status);
        var policyId = env.KeepAwake.List().Single().Policy.PolicyId;
        Assert.Contains(policyId, env.KeepAwake.ActivePoliciesFor(d1).Select(p => p.PolicyId));

        env.Time.Now = env.Time.Now.AddMinutes(11); // past absolute expiry
        Assert.Empty(env.KeepAwake.ActivePoliciesFor(d1));
        Assert.True(env.KeepAwake.List().Single().Expired);
    }

    [Fact]
    public void Resync_dedup_prevents_duplicate_pending_set_events()
    {
        var (env, d1, _) = TwoDevices();
        Assert.Equal(201, env.KeepAwake.Create("admin", "device", d1, 60, null, false).Status);
        var policy = env.KeepAwake.List().Single().Policy;
        Assert.True(env.KeepAwake.HasPendingSet(d1, policy.PolicyId));
        Assert.False(env.KeepAwake.HasPendingSet(d1, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Group_scope_targets_only_group_members()
    {
        var (env, d1, d2) = TwoDevices();
        using (var conn = env.Database.Open())
        {
            conn.Run("INSERT INTO groups(id, name) VALUES (7, 'Lab')");
            conn.Run("INSERT INTO group_members(device_id, group_id) VALUES ($d, 7)", ("$d", d1));
        }
        Assert.Equal(201, env.KeepAwake.Create("admin", "group", "7", 30, null, false).Status);
        Assert.Equal(1, env.KeepAwake.List().Single().DesiredCount);
        using var conn2 = env.Database.Open();
        Assert.Equal(1L, SetEvents(conn2, d1));
        Assert.Equal(0L, SetEvents(conn2, d2));
        Assert.Equal(400, env.KeepAwake.Create("admin", "group", "99", 30, null, false).Status);
    }

    static long SetEvents(Microsoft.Data.Sqlite.SqliteConnection conn, string deviceId) =>
        Convert.ToInt64(conn.ScalarForTests(
            $"SELECT COUNT(*) FROM outbox WHERE device_id = '{deviceId}' AND event_json LIKE '%keepawake.set%'"));

    static long ClearEvents(Microsoft.Data.Sqlite.SqliteConnection conn, string deviceId) =>
        Convert.ToInt64(conn.ScalarForTests(
            $"SELECT COUNT(*) FROM outbox WHERE device_id = '{deviceId}' AND event_json LIKE '%keepawake.clear%'"));
}

public class DeviceStatusTests
{
    [Fact]
    public void Labels_prefer_active_user_then_computer_name()
    {
        using var env = new TestEnv();
        var device = SeedDevice(env, "rhs-9393");

        env.Devices.UpdateStatus(device, Status("rhs-9393", null), "{}");
        Assert.Equal("No signed-in user (rhs-9393)", env.Devices.Get(device)!.Label);

        env.Devices.UpdateStatus(device, Status("rhs-9393", "james.doe"), "{}");
        Assert.Equal("james.doe (rhs-9393)", env.Devices.Get(device)!.Label);
    }

    [Fact]
    public void Disconnect_past_stale_window_becomes_offline()
    {
        using var env = new TestEnv();
        var device = SeedDevice(env, "rhs-off");
        env.Devices.UpdateStatus(device, Status("rhs-off", null), "{}");

        env.Time.Now = env.Time.Now.AddSeconds(env.Cfg.StaleAfterSeconds + 5);
        Assert.Equal("offline", env.Devices.Get(device)!.State);
    }

    [Fact]
    public void Search_filters_by_computer_user_group_and_id_with_pagination()
    {
        using var env = new TestEnv();
        var a = SeedDevice(env, "rhs-aaa");
        var b = SeedDevice(env, "rhs-bbb");
        var c = SeedDevice(env, "room-only");
        env.Devices.UpdateStatus(a, Status("rhs-aaa", "alice"), "{}");
        env.Devices.UpdateStatus(b, Status("rhs-bbb", "bob"), "{}");
        env.Devices.UpdateStatus(c, Status("room-only", null), "{}");
        using (var conn = env.Database.Open())
        {
            conn.Run("INSERT INTO groups(id, name) VALUES (11, 'Room 204')");
            conn.Run("INSERT INTO group_members(device_id, group_id) VALUES ($d, 11)", ("$d", c));
        }

        Assert.Equal(1, env.Devices.List("alice", 1, 50).Total);
        Assert.Equal(1, env.Devices.List("Room 204", 1, 50).Total);
        Assert.Equal(1, env.Devices.List(a, 1, 50).Total);            // exact device_id
        Assert.Equal(2, env.Devices.List("rhs-", 1, 50).Total);       // prefix on names

        var page1 = env.Devices.List(null, 1, 2);
        var page2 = env.Devices.List(null, 2, 2);
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
    }

    static string SeedDevice(TestEnv env, string name) =>
        env.Enrollment.Enroll(env.Enrollment.CreateToken("admin", name, 24, 1, []).Token!, name).Result!.DeviceId;

    static DeviceStatusPayload Status(string computer, string? user) =>
        new(computer, user, null, "ready", null, "Speakers", true, false, 40, false, 50,
            0, 0, 0, new KeepAwakeStatus(false, false, 0), null, "running");
}

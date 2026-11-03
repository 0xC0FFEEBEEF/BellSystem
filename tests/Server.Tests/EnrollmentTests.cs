using SchoolBell.Server;
using System.Text.Json;
using SchoolBell.Contracts;

namespace Server.Tests;

public class EnrollmentTests
{
    [Fact]
    public void Token_create_and_enroll_returns_exact_protocol_shape_once()
    {
        using var env = new TestEnv();
        var created = env.Enrollment.CreateToken("admin", "lab tray", 24, 1, []);
        Assert.Equal(201, created.Status);
        Assert.NotNull(created.Token);
        Assert.Equal(48, created.Token!.Length); // 24 bytes hex

        var res = env.Enrollment.Enroll(created.Token, "rhs-9393");
        Assert.Equal(201, res.Status);
        Assert.NotNull(res.Result);
        Assert.True(Guid.TryParse(res.Result!.DeviceId, out _));
        Assert.Equal(64, res.Result.DeviceSecret.Length);                 // 32 bytes hex
        Assert.Equal(64, res.Result.ServerTrustPin.Length);               // 32 bytes hex
        Assert.DoesNotContain(" ", res.Result.DeviceSecret);
    }

    [Fact]
    public void Token_is_single_use()
    {
        using var env = new TestEnv();
        var created = env.Enrollment.CreateToken("admin", "single", 24, 1, []);
        Assert.Equal(201, env.Enrollment.Enroll(created.Token!, "d-a").Status);
        var second = env.Enrollment.Enroll(created.Token!, "d-b");
        Assert.Equal(403, second.Status);
        Assert.Equal("enrollment token already used", second.Detail);
    }

    [Fact]
    public void Token_multi_use_respects_max_uses()
    {
        using var env = new TestEnv();
        var created = env.Enrollment.CreateToken("admin", "bulk", 24, 2, []);
        Assert.Equal(201, env.Enrollment.Enroll(created.Token!, "d-a").Status);
        Assert.Equal(201, env.Enrollment.Enroll(created.Token!, "d-b").Status);
        Assert.Equal(403, env.Enrollment.Enroll(created.Token!, "d-c").Status);
    }

    [Fact]
    public void Token_expiry_is_absolute()
    {
        using var env = new TestEnv();
        var created = env.Enrollment.CreateToken("admin", "short", 1, 1, []);
        env.Time.Now = env.Time.Now.AddMinutes(61);
        var res = env.Enrollment.Enroll(created.Token!, "d-a");
        Assert.Equal(403, res.Status);
        Assert.Equal("enrollment token expired", res.Detail);
    }

    [Fact]
    public void Invalid_or_revoked_token_is_rejected()
    {
        using var env = new TestEnv();
        Assert.Equal(403, env.Enrollment.Enroll("deadbeef", "d-a").Status);

        var created = env.Enrollment.CreateToken("admin", "rev", 24, 1, []);
        var rev = env.Enrollment.RevokeToken("admin", created.Info!.Id);
        Assert.Equal(200, rev.Status);
        var res = env.Enrollment.Enroll(created.Token!, "d-a");
        Assert.Equal(403, res.Status);
        Assert.Equal("enrollment token revoked", res.Detail);
    }

    [Fact]
    public void Revoked_device_cannot_reenroll()
    {
        using var env = new TestEnv();
        var t1 = env.Enrollment.CreateToken("admin", "t1", 24, 1, []);
        var d = env.Enrollment.Enroll(t1.Token!, "rhs-77").Result!;
        Assert.Equal(200, env.Enrollment.RevokeDevice("admin", d.DeviceId).Status);

        var t2 = env.Enrollment.CreateToken("admin", "t2", 24, 1, []);
        var res = env.Enrollment.Enroll(t2.Token!, "rhs-77");
        Assert.Equal(403, res.Status);
        Assert.Equal("revoked", res.Detail);
    }

    [Fact]
    public void Reenroll_known_active_device_keeps_device_id_and_rotates_secret()
    {
        using var env = new TestEnv();
        var t1 = env.Enrollment.CreateToken("admin", "t1", 24, 1, []);
        var first = env.Enrollment.Enroll(t1.Token!, "rhs-78").Result!;
        var t2 = env.Enrollment.CreateToken("admin", "t2", 24, 1, []);
        var second = env.Enrollment.Enroll(t2.Token!, "RHS-78").Result!; // name match is case-insensitive
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.NotEqual(first.DeviceSecret, second.DeviceSecret);
    }

    [Fact]
    public void Token_group_scope_adds_membership()
    {
        using var env = new TestEnv();
        using (var conn = env.Database.Open())
            conn.Run("INSERT INTO groups(id, name) VALUES (3, 'Room 204')");
        var created = env.Enrollment.CreateToken("admin", "scoped", 24, 1, [3]);
        var d = env.Enrollment.Enroll(created.Token!, "rhs-99").Result!;
        Assert.Contains(3L, env.Devices.GroupsOf(d.DeviceId));
    }

    [Fact]
    public void Enroll_tokens_and_secrets_never_appear_in_logs()
    {
        using var env = new TestEnv();
        var created = env.Enrollment.CreateToken("admin", "quiet", 24, 1, []);
        var res = env.Enrollment.Enroll(created.Token!, "rhs-quiet");
        var secret = res.Result!.DeviceSecret;
        var pin = res.Result.ServerTrustPin;

        string all = env.Logs.AllText;
        Assert.DoesNotContain(created.Token!, all);
        Assert.DoesNotContain(secret, all);
        Assert.DoesNotContain(pin, all);
        // hash forms too (defense in depth)
        Assert.DoesNotContain(Util.Sha256Hex(created.Token!), all);
    }

    [Fact]
    public void Login_passwords_never_appear_in_logs()
    {
        using var env = new TestEnv();
        var created = env.Auth.CreateUser("test", "alice", "correct-horse99!", "viewer", [], false);
        Assert.Equal(201, created.Status);
        var res = env.Auth.Login("alice", "correct-horse99!", "127.0.0.1");
        Assert.Equal(200, res.Status);
        Assert.DoesNotContain("correct-horse99!", env.Logs.AllText);
        Assert.DoesNotContain(res.SessionToken!, env.Logs.AllText);
    }
}

public class OutboxTests
{
    [Fact]
    public void Replay_from_cursor_delivers_only_later_events_in_order()
    {
        using var env = new TestEnv();
        SeedDevice(env, "dev-a");
        var e1 = env.Outbox.Enqueue("dev-a", ControlEnvelope.Create("test.custom"));
        var e2 = env.Outbox.Enqueue("dev-a", ControlEnvelope.Create("test.custom"));
        var e3 = env.Outbox.Enqueue("dev-a", ControlEnvelope.Create("test.custom"));

        var replay = env.Outbox.Replay("dev-a", e1.Envelope.EventId);
        Assert.Equal(2, replay.Count);
        Assert.Contains(e2.Envelope.EventId!, replay[0]);
        Assert.Contains(e3.Envelope.EventId!, replay[1]);

        // null cursor replays all
        Assert.Equal(3, env.Outbox.Replay("dev-a", null).Count);
        // unknown (dropped) cursor replays the remaining bounded window
        Assert.Equal(3, env.Outbox.Replay("dev-a", "not-a-known-event").Count);
    }

    [Fact]
    public void Outbox_is_bounded_per_device_oldest_dropped_with_visible_diagnostic()
    {
        using var env = new TestEnv(outboxCap: 5);
        SeedDevice(env, "dev-cap");
        var first = env.Outbox.Enqueue("dev-cap", ControlEnvelope.Create("test.custom"));
        for (int i = 0; i < 6; i++)
            env.Outbox.Enqueue("dev-cap", ControlEnvelope.Create("test.custom"));

        var replay = env.Outbox.Replay("dev-cap", null);
        Assert.Equal(5, replay.Count);
        Assert.DoesNotContain(first.Envelope.EventId!, replay); // oldest dropped
        Assert.Equal(2, env.Outbox.TotalDropped);
        Assert.Contains("outbox cap", env.Logs.AllText); // visible diagnostic logged
        var (total, max) = env.Outbox.Depth();
        Assert.Equal(5, max);
    }

    private static void SeedDevice(TestEnv env, string deviceId)
    {
        using var conn = env.Database.Open();
        conn.Run("""
            INSERT INTO devices(device_id, name, secret, status, revoked, first_seen)
            VALUES ($d, $n, 'aa', 'active', 0, 1)
            """, ("$d", deviceId), ("$n", deviceId));
    }
}

public class ReceiptsTests
{
    [Fact]
    public void Ingest_is_idempotent_per_device_ref_state()
    {
        using var env = new TestEnv();
        SeedDevice(env, "dev-r");
        int first = env.Receipts.Ingest("dev-r", "occ-1", ReceiptState.Completed, 1000, null);
        int second = env.Receipts.Ingest("dev-r", "occ-1", ReceiptState.Completed, 1000, null);
        Assert.Equal(1, first);
        Assert.Equal(0, second);
        using var conn = env.Database.Open();
        Assert.Equal(1L, Convert.ToInt64(conn.ScalarForTests("SELECT COUNT(*) FROM receipts")));
        // different state for same device+ref is a distinct row (honest reporting)
        Assert.Equal(1, env.Receipts.Ingest("dev-r", "occ-1", ReceiptState.Suppressed, 1001, "live_speech"));
    }

    [Fact]
    public void Invalid_state_is_a_protocol_violation()
    {
        using var env = new TestEnv();
        Assert.Equal(-1, env.Receipts.Ingest("dev-r", "occ-1", "definitely-not-a-state", 1, null));
    }

    private static void SeedDevice(TestEnv env, string deviceId)
    {
        using var conn = env.Database.Open();
        conn.Run("""
            INSERT INTO devices(device_id, name, secret, status, revoked, first_seen)
            VALUES ($d, $n, 'aa', 'active', 0, 1)
            """, ("$d", deviceId), ("$n", deviceId));
    }
}

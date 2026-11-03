using SchoolBell.Server;
using System.Text;

namespace Server.Tests;

public class AuthServiceTests
{
    [Fact]
    public void Bootstrap_prints_setup_code_once_and_setup_creates_admin()
    {
        using var env = new TestEnv();
        var console = new StringWriter();
        var saved = Console.Out;
        try
        {
            Console.SetOut(console);
            env.Auth.EnsureBootstrap();
            env.Auth.EnsureBootstrap(); // second run: code already stored, not reprinted
        }
        finally { Console.SetOut(saved); }

        var printed = console.ToString();
        Assert.Equal(1, CountOccurrences(printed, "SETUP CODE: "));
        var code = printed.Split("SETUP CODE: ")[1].Trim().Split('\n')[0].Trim();
        Assert.Equal(24, code.Length); // 24 hex chars

        // wrong code rejected
        Assert.Equal(403, env.Auth.Setup("wrong-code-xx", "admin", "correct-horse99!").Status);
        // correct code creates admin and consumes the code
        Assert.Equal(201, env.Auth.Setup(code, "admin", "correct-horse99!").Status);
        // consumed: further setup attempts are 403 regardless of payload
        Assert.Equal(403, env.Auth.Setup(code, "admin2", "correct-horse99!").Status);
    }

    [Fact]
    public void Password_policy_requires_12_chars_on_admin_created_accounts()
    {
        using var env = new TestEnv();
        var res = env.Auth.CreateUser("admin", "alice", "short9!", "viewer", [], false);
        Assert.Equal(400, res.Status);
        Assert.Contains("12", res.Detail!);
        Assert.Equal(201, env.Auth.CreateUser("admin", "alice", "correct-horse99!", "viewer", [], false).Status);
        // username policy
        Assert.Equal(400, env.Auth.CreateUser("admin", "BAD USER!", "correct-horse99!", "viewer", [], false).Status);
        // duplicate
        Assert.Equal(400, env.Auth.CreateUser("admin", "alice", "another-good99!!", "viewer", [], false).Status);
    }

    [Fact]
    public void Login_rate_limit_blocks_9th_attempt_then_recovers_after_window()
    {
        using var env = new TestEnv();
        Assert.Equal(201, env.Auth.CreateUser("admin", "bob", "correct-horse99!", "viewer", [], false).Status);

        for (int i = 0; i < 8; i++)
        {
            var res = env.Auth.Login("bob", "wrong-password-xx", "10.0.0.5");
            Assert.Equal(401, res.Status);
        }
        var blocked = env.Auth.Login("bob", "wrong-password-xx", "10.0.0.5");
        Assert.Equal(429, blocked.Status);
        Assert.Contains("too many failed", blocked.Detail!);

        // after the 5-minute window the account recovers with the right password
        env.Time.Now = env.Time.Now.AddMinutes(6);
        var ok = env.Auth.Login("bob", "correct-horse99!", "10.0.0.5");
        Assert.Equal(200, ok.Status);
    }

    [Fact]
    public void Login_unknown_user_is_401_not_404_and_sessions_resolve_and_logout()
    {
        using var env = new TestEnv();
        env.Auth.CreateUser("admin", "carol", "correct-horse99!", "operator", [3], true);
        Assert.Equal(401, env.Auth.Login("nobody", "whatever-correct9!", "10.0.0.5").Status);

        var res = env.Auth.Login("carol", "correct-horse99!", "10.0.0.5");
        Assert.Equal(200, res.Status);
        var user = env.Auth.ResolveSession(res.SessionToken);
        Assert.NotNull(user);
        Assert.Equal("operator", user!.Role);
        Assert.Equal([3L], user.GroupScope);
        Assert.True(user.SchoolwideBroadcast);

        env.Auth.Logout(user);
        Assert.Null(env.Auth.ResolveSession(res.SessionToken));
    }

    [Fact]
    public void Password_hash_round_trip_and_tamper()
    {
        var stored = Passwords.Hash("correct-horse99!");
        Assert.StartsWith("pbkdf2$210000$", stored);
        Assert.True(Passwords.Verify(stored, "correct-horse99!"));
        Assert.False(Passwords.Verify(stored, "Correct-Horse99!"));
        Assert.False(Passwords.Verify("pbkdf2$1$AAAA$BBBB", "x"));
    }

    static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}

public class TemplateValidationTests
{
    static TemplateInput Valid(string name = "Regular", string start = "08:30", string end = "09:20",
        int warn = 5) =>
        new(name, new SchoolBell.Contracts.TargetScope(true, null, null),
            [new PeriodInput("Period 1", start, end, warn, "std-bell", "announcement", 3)],
            [1, 2, 3, 4, 5]);

    [Fact]
    public void Valid_template_creates_and_lists()
    {
        using var env = new TestEnv();
        Assert.Equal(201, env.Templates.Create(env.Admin(), Valid()).Status);
        var list = env.Templates.ListTemplates();
        Assert.Single(list);
        Assert.Single(list[0].Periods);
        Assert.Equal(5, list[0].Weekdays.Count);
    }

    [Theory]
    [InlineData("8:30", "09:20", 5, "HH:MM")]           // non-padded hour
    [InlineData("24:00", "25:00", 5, "HH:MM")]          // out of range
    [InlineData("09:00", "08:00", 5, "end must be after start")] // end <= start
    [InlineData("08:30", "09:20", 241, "warn_min")]    // warn out of range
    [InlineData("08:30", "09:20", -1, "warn_min")]
    public void Invalid_periods_are_rejected_with_detail(string start, string end, int warn, string expectedFragment)
    {
        using var env = new TestEnv();
        var bad = Valid(start: start, end: end, warn: warn);
        var res = env.Templates.Create(env.Admin(), bad);
        Assert.Equal(400, res.Status);
        Assert.Contains(expectedFragment, res.Detail!);
    }

    [Fact]
    public void Duplicate_name_is_rejected()
    {
        using var env = new TestEnv();
        Assert.Equal(201, env.Templates.Create(env.Admin(), Valid("Dup")).Status);
        var res = env.Templates.Create(env.Admin(), Valid("Dup"));
        Assert.Equal(400, res.Status);
        Assert.Contains("unique", res.Detail!);
    }

    [Fact]
    public void Unknown_sound_and_volume_policy_rejected()
    {
        using var env = new TestEnv();
        var badSound = Valid();
        badSound = badSound with { Periods = [new PeriodInput("P", "08:30", "09:20", 0, "nope-sound", "announcement", 3)] };
        Assert.Equal(400, env.Templates.Create(env.Admin(), badSound).Status);

        var badPolicy = Valid();
        badPolicy = badPolicy with { Periods = [new PeriodInput("P", "08:30", "09:20", 0, "std-bell", "shout", 3)] };
        Assert.Equal(400, env.Templates.Create(env.Admin(), badPolicy).Status);
    }

    [Fact]
    public void Target_must_set_exactly_one_scope_and_groups_must_exist()
    {
        using var env = new TestEnv();
        var none = Valid("T1") with { Target = new SchoolBell.Contracts.TargetScope(null, null, null) };
        Assert.Equal(400, env.Templates.Create(env.Admin(), none).Status);
        var both = Valid("T2") with { Target = new SchoolBell.Contracts.TargetScope(true, [1], null) };
        Assert.Equal(400, env.Templates.Create(env.Admin(), both).Status);
        var unknownGroup = Valid("T3") with { Target = new SchoolBell.Contracts.TargetScope(null, [42], null) };
        var res = env.Templates.Create(env.Admin(), unknownGroup);
        Assert.Equal(400, res.Status);
        Assert.Contains("unknown group", res.Detail!);
    }

    [Fact]
    public void Weekday_range_validated()
    {
        using var env = new TestEnv();
        var bad = Valid("WD") with { Weekdays = [7] };
        Assert.Equal(400, env.Templates.Create(env.Admin(), bad).Status);
    }

    [Fact]
    public void Calendar_overrides_and_holidays_validate_and_delete()
    {
        using var env = new TestEnv();
        Assert.Equal(400, env.Templates.UpsertOverride(env.Admin(), "11-05-2026", null).Status);
        Assert.Equal(400, env.Templates.UpsertOverride(env.Admin(), "2026-11-05", 99).Status); // unknown template
        Assert.Equal(200, env.Templates.UpsertOverride(env.Admin(), "2026-11-05", null).Status);
        Assert.Equal(200, env.Templates.UpsertOverride(env.Admin(), "2026-11-05", null).Status); // upsert ok
        Assert.Equal(200, env.Templates.DeleteOverride(env.Admin(), "2026-11-05").Status);
        Assert.Equal(404, env.Templates.DeleteOverride(env.Admin(), "2026-11-05").Status);

        Assert.Equal(400, env.Templates.AddHoliday(env.Admin(), "2026-12-25", "").Status);
        Assert.Equal(200, env.Templates.AddHoliday(env.Admin(), "2026-12-25", "Christmas").Status);
        Assert.Equal(404, env.Templates.DeleteHoliday(env.Admin(), "2026-12-26").Status);
    }
}

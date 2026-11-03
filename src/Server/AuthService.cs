using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

public sealed record AuthUser(int Id, string Username, string Role, List<long> GroupScope, bool SchoolwideBroadcast);
public sealed record CurrentUser(int Id, string Username, string Role, List<long> GroupScope, bool SchoolwideBroadcast, string SessionId);

public sealed record AuthResult(int Status, string? Detail, AuthUser? User = null, string? SessionToken = null)
{
    public bool Ok => Status == 200;
}

/// Bootstrap, setup, login (rate-limited), sessions, logout.
/// Never logs passwords or session tokens.
public sealed class AuthService
{
    public const int MinPasswordLength = 12;
    public const int MaxFailedAttempts = 8;          // per username+IP within the window
    public const long LockoutWindowMs = 5 * 60_000;  // 5 minutes
    public const long SessionLifetimeMs = 12 * 60 * 60_000;

    private static readonly Regex UsernameRe = new("^[a-z0-9._-]{2,32}$", RegexOptions.Compiled);

    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthService> _log;
    private readonly Dictionary<string, List<long>> _recentFailures = new(); // key -> failure times (unix ms)
    private readonly object _failLock = new();

    public AuthService(Db db, ServerConfig cfg, TimeProvider time, ILogger<AuthService> log)
    {
        _db = db; _cfg = cfg; _time = time; _log = log;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// Startup bootstrap: one-time setup code when no users exist. Printed ONCE to stdout.
    public void EnsureBootstrap()
    {
        using var conn = _db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM users";
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return;
        }
        if (Db.GetSetting(conn, "setup_code_hash") is not null) return;

        var code = _cfg.TestSetupCode ?? Util.RandomHex(12); // 24 hex chars
        Db.SetSetting(conn, "setup_code_hash", Util.Sha256Hex(code));
        Console.WriteLine($"SETUP CODE: {code}");
        _log.LogInformation("bootstrap setup code generated (value not logged)");
    }

    public AuthResult Setup(string code, string username, string password)
    {
        using var conn = _db.Open();
        var storedHash = Db.GetSetting(conn, "setup_code_hash");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM users";
            var anyUsers = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            if (storedHash is null || anyUsers)
                return new(403, "setup already completed");
        }
        if (!Util.HexEqualsConstantTime(Util.Sha256Hex(code), storedHash!))
            return new(403, "invalid setup code");

        var validation = ValidateAccount(username, password);
        if (validation is not null) return validation;

        InsertUser(conn, username, password, "admin", "[]", false);
        conn.Run("DELETE FROM settings WHERE key = 'setup_code_hash'");
        Db.SetSetting(conn, "setup_consumed", "1");
        Audit.Add(_db, "setup", "setup.admin_created", username);
        _log.LogInformation("bootstrap admin account created: {Username}", username);
        return new(201, null);
    }

    public AuthResult Login(string username, string password, string ip)
    {
        var rateKey = $"{username.ToLowerInvariant()}|{ip}";
        var now = NowMs;
        lock (_failLock)
        {
            if (_recentFailures.TryGetValue(rateKey, out var list))
            {
                list.RemoveAll(t => t < now - LockoutWindowMs);
                if (list.Count >= MaxFailedAttempts)
                    return new(429, "too many failed login attempts; wait 5 minutes");
            }
        }

        AuthUser? user;
        using (var conn = _db.Open())
        {
            user = FindUser(conn, username);
            if (user is null)
            {
                RecordFailure(rateKey, now);
                return new(401, "invalid username or password");
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT failed_attempts, lockout_until FROM users WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", user.Id);
            long lockout;
            using (var r = cmd.ExecuteReader())
            {
                r.Read();
                lockout = r.IsDBNull(1) ? 0L : r.GetInt64(1);
            }
            if (lockout > now)
                return new(429, "account temporarily locked; wait 5 minutes");
        }

        if (!Passwords.Verify(StoredHash(username), password))
        {
            RecordFailure(rateKey, now);
            using var conn = _db.Open();
            conn.Run("UPDATE users SET failed_attempts = failed_attempts + 1 WHERE id = $id", ("$id", user.Id));
            var attempts = Convert.ToInt32(conn.Scalar("SELECT failed_attempts FROM users WHERE id = $id", ("$id", user.Id)) ?? 0);
            if (attempts >= MaxFailedAttempts)
            {
                // DB-side lockout (survives restarts and covers other source IPs)
                conn.Run("UPDATE users SET lockout_until = $l, failed_attempts = 0 WHERE id = $id",
                    ("$l", now + LockoutWindowMs), ("$id", user.Id));
            }
            return new(401, "invalid username or password");
        }

        // success: clear counters
        lock (_failLock) _recentFailures.Remove(rateKey);
        var token = Util.RandomHex(32);
        var sessionId = Util.Sha256Hex(token);
        using (var conn = _db.Open())
        {
            conn.Run("UPDATE users SET failed_attempts = 0, lockout_until = NULL WHERE id = $id", ("$id", user.Id));
            conn.Run("INSERT INTO sessions(id, user_id, created, expires) VALUES ($id, $u, $c, $e)",
                ("$id", sessionId), ("$u", user.Id), ("$c", now), ("$e", now + SessionLifetimeMs));
        }
        Audit.Add(_db, user.Username, "auth.login", null);
        return new(200, null, user, token);
    }

    public CurrentUser? ResolveSession(string? sessionToken)
    {
        if (string.IsNullOrEmpty(sessionToken)) return null;
        var id = Util.Sha256Hex(sessionToken);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.expires, u.id, u.username, u.role, u.group_scope, u.schoolwide_broadcast
            FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var expires = r.GetInt64(1);
        if (expires < NowMs)
        {
            conn.Run("DELETE FROM sessions WHERE id = $id", ("$id", id));
            return null;
        }
        return new CurrentUser(r.GetInt32(2), r.GetString(3), r.GetString(4),
            ParseGroupScope(r.GetString(5)), r.GetInt32(6) == 1, r.GetString(0));
    }

    public void Logout(CurrentUser user)
    {
        using var conn = _db.Open();
        conn.Run("DELETE FROM sessions WHERE id = $id", ("$id", user.SessionId));
        Audit.Add(_db, user.Username, "auth.logout", null);
    }

    public AuthResult CreateUser(string actor, string username, string password, string role,
        List<long> groupScope, bool schoolwideBroadcast)
    {
        var validation = ValidateAccount(username, password);
        if (validation is not null) return validation;
        if (role is not ("admin" or "operator" or "viewer"))
            return new(400, "role must be admin, operator or viewer");

        using var conn = _db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = $u";
            cmd.Parameters.AddWithValue("$u", username);
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
                return new(400, "username already exists");
        }
        var id = InsertUser(conn, username, password, role, System.Text.Json.JsonSerializer.Serialize(groupScope), schoolwideBroadcast);
        Audit.Add(_db, actor, "user.create", $"{username} role={role}");
        return new(201, null, new AuthUser(id, username, role, groupScope, schoolwideBroadcast));
    }

    public List<AuthUser> ListUsers()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, role, group_scope, schoolwide_broadcast FROM users ORDER BY username";
        using var r = cmd.ExecuteReader();
        var list = new List<AuthUser>();
        while (r.Read())
            list.Add(new AuthUser(r.GetInt32(0), r.GetString(1), r.GetString(2),
                ParseGroupScope(r.GetString(3)), r.GetInt32(4) == 1));
        return list;
    }

    public (int Status, string? Detail) UpdateUser(string actor, int id, string? role, List<long>? groupScope,
        bool? schoolwideBroadcast, string? password)
    {
        using var conn = _db.Open();
        if (role is not null && role is not ("admin" or "operator" or "viewer"))
            return (400, "role must be admin, operator or viewer");
        if (password is not null && password.Length < MinPasswordLength)
            return (400, $"password must be at least {MinPasswordLength} characters");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET
              role = COALESCE($role, role),
              group_scope = COALESCE($gs, group_scope),
              schoolwide_broadcast = COALESCE($sw, schoolwide_broadcast),
              pw_hash = COALESCE($pw, pw_hash)
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$gs", groupScope is null ? null : System.Text.Json.JsonSerializer.Serialize(groupScope));
        cmd.Parameters.AddWithValue("$sw", schoolwideBroadcast is null ? null : (schoolwideBroadcast.Value ? 1 : 0));
        cmd.Parameters.AddWithValue("$pw", password is null ? null : Passwords.Hash(password));
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0) return (404, "user not found");
        Audit.Add(_db, actor, "user.update", $"user_id={id}" + (role is null ? "" : $" role={role}"));
        return (200, null);
    }

    public (int Status, string? Detail) DeleteUser(string actor, int id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0) return (404, "user not found");
        Audit.Add(_db, actor, "user.delete", $"user_id={id}");
        return (200, null);
    }

    public static List<long> ParseGroupScope(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<long>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public static string? ValidateUsername(string username) =>
        UsernameRe.IsMatch(username) ? null : "username must match ^[a-z0-9._-]{2,32}$";

    private AuthResult? ValidateAccount(string username, string password)
    {
        if (ValidateUsername(username) is string uerr) return new(400, uerr);
        if (password.Length < MinPasswordLength)
            return new(400, $"password must be at least {MinPasswordLength} characters");
        return null;
    }

    private int InsertUser(SqliteConnection conn, string username, string password, string role,
        string groupScopeJson, bool schoolwide)
    {
        conn.Run("""
            INSERT INTO users(username, pw_hash, role, group_scope, schoolwide_broadcast, created_at)
            VALUES ($u, $p, $r, $g, $s, $t)
            """,
            ("$u", username), ("$p", Passwords.Hash(password)), ("$r", role), ("$g", groupScopeJson),
            ("$s", schoolwide ? 1 : 0), ("$t", NowMs));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM users WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string StoredHash(string username)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pw_hash FROM users WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteScalar() as string ?? "none";
    }

    private AuthUser? FindUser(SqliteConnection conn, string username)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, role, group_scope, schoolwide_broadcast FROM users WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AuthUser(r.GetInt32(0), r.GetString(1), r.GetString(2),
            ParseGroupScope(r.GetString(3)), r.GetInt32(4) == 1);
    }

    private void RecordFailure(string rateKey, long now)
    {
        lock (_failLock)
        {
            if (!_recentFailures.TryGetValue(rateKey, out var list))
                _recentFailures[rateKey] = list = [];
            list.Add(now);
        }
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

public sealed record EnrollTokenInfo(int Id, string Label, long ExpiresAt, int MaxUses, int Used,
    List<long> GroupScope, string? CreatedBy, long CreatedAt, bool Revoked);
public sealed record TokenOutcome(int Status, string? Detail, EnrollTokenInfo? Info = null, string? Token = null);
public sealed record EnrollResponse(string DeviceId, string DeviceSecret, string ServerTrustPin);
public sealed record EnrollOutcome(int Status, string? Detail, EnrollResponse? Result = null);

/// Enrollment per PROTOCOL.md §2. Token values and device secrets are NEVER logged.
public sealed class EnrollmentService
{
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly ILogger<EnrollmentService> _log;

    public EnrollmentService(Db db, TimeProvider time, ILogger<EnrollmentService> log)
    {
        _db = db; _time = time; _log = log;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public TokenOutcome CreateToken(string actor, string label, int ttlHours, int maxUses, List<long> groupScope)
    {
        if (string.IsNullOrWhiteSpace(label)) return new(400, "label is required");
        if (ttlHours <= 0 || ttlHours > 24 * 14) return new(400, "ttl_hours must be between 1 and 336");
        if (maxUses < 1 || maxUses > 10_000) return new(400, "max_uses must be between 1 and 10000");

        using var conn = _db.Open();
        foreach (var gid in groupScope)
        {
            if (!conn.Exists("SELECT COUNT(*) FROM groups WHERE id = $g", ("$g", gid)))
                return new(400, $"unknown group id {gid}");
        }

        var token = Util.RandomHex(24);
        var expiresAt = NowMs + ttlHours * 3600_000L;
        var id = Convert.ToInt32(conn.Scalar("""
            INSERT INTO enrollment_tokens(token_hash, expires_at, max_uses, group_scope, created_by, label, created_at)
            VALUES ($h, $e, $m, $g, $c, $l, $t) RETURNING id
            """,
            ("$h", Util.Sha256Hex(token)), ("$e", expiresAt), ("$m", maxUses),
            ("$g", JsonSerializer.Serialize(groupScope)), ("$c", actor), ("$l", label), ("$t", NowMs)));
        Audit.Add(_db, actor, "enrollment_token.create", $"id={id} label={label} uses={maxUses} ttl_h={ttlHours}");
        // Only the id is written to any log; the token itself is returned once to the caller.
        _log.LogInformation("enrollment token {Id} label={Label} created (secret not logged)", id, label);
        var info = new EnrollTokenInfo(id, label, expiresAt, maxUses, 0, groupScope, actor, NowMs, false);
        return new(201, null, info, token);
    }

    public List<EnrollTokenInfo> ListTokens()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, expires_at, max_uses, used, group_scope, created_by, created_at, revoked
            FROM enrollment_tokens ORDER BY id DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<EnrollTokenInfo>();
        while (r.Read())
            list.Add(new EnrollTokenInfo(r.GetInt32(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3),
                r.GetInt32(4), AuthService.ParseGroupScope(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7), r.GetInt32(8) == 1));
        return list;
    }

    public (int Status, string? Detail) RevokeToken(string actor, int id)
    {
        using var conn = _db.Open();
        if (conn.Run("UPDATE enrollment_tokens SET revoked = 1 WHERE id = $id", ("$id", id)) == 0)
            return (404, "enrollment token not found");
        Audit.Add(_db, actor, "enrollment_token.revoke", $"id={id}");
        return (200, null);
    }

    /// POST /api/enroll (no office auth). Device identity: name match (case-insensitive).
    /// Unknown name -> fresh device_id + secret. Known non-revoked -> new secret, same device_id.
    /// Revoked device -> 403 revoked.
    public EnrollOutcome Enroll(string token, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(deviceName))
            return new(400, "token and device_name are required");

        var tokenHash = Util.Sha256Hex(token);
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        int tokenId = 0; long expiresAt = 0; int maxUses = 0, used = 0, revokedFlag = 0; string groupScopeJson = "[]";
        bool tokenFound;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, expires_at, max_uses, used, revoked, group_scope FROM enrollment_tokens WHERE token_hash = $h";
            cmd.Parameters.AddWithValue("$h", tokenHash);
            using var r = cmd.ExecuteReader();
            tokenFound = r.Read();
            if (tokenFound)
            {
                tokenId = r.GetInt32(0); expiresAt = r.GetInt64(1); maxUses = r.GetInt32(2);
                used = r.GetInt32(3); revokedFlag = r.GetInt32(4); groupScopeJson = r.GetString(5);
            }
        }
        if (!tokenFound)
        {
            // Audit on the same connection/transaction (never a second writer while one is open).
            Audit.Add(conn, tx, "enroll", "enrollment_token.reject_invalid", null);
            tx.Commit();
            return new(403, "invalid enrollment token");
        }

        if (revokedFlag == 1) return new(403, "enrollment token revoked");
        if (expiresAt <= NowMs) return new(403, "enrollment token expired");
        if (used >= maxUses) return new(403, "enrollment token already used");

        conn.Run(tx, "UPDATE enrollment_tokens SET used = used + 1 WHERE id = $id", ("$id", tokenId));

        // name identity: revoked devices cannot re-enroll (PROTOCOL.md §2)
        string deviceId; string secret;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT device_id, revoked FROM devices WHERE name = $n COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$n", deviceName);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                if (r.GetInt32(1) == 1)
                    return new(403, "revoked");
                deviceId = r.GetString(0);
                secret = Util.RandomHex(32);
                conn.Run(tx, "UPDATE devices SET secret = $s, status = 'active', last_seen = $t WHERE device_id = $d",
                    ("$s", secret), ("$t", NowMs), ("$d", deviceId));
            }
            else
            {
                deviceId = Guid.NewGuid().ToString();
                secret = Util.RandomHex(32);
                conn.Run(tx, """
                    INSERT INTO devices(device_id, name, secret, status, revoked, first_seen, last_seen)
                    VALUES ($d, $n, $s, 'active', 0, $t, $t)
                    """,
                    ("$d", deviceId), ("$n", deviceName), ("$s", secret), ("$t", NowMs));
            }
        }

        // scoped token: join the token's groups
        foreach (var gid in AuthService.ParseGroupScope(groupScopeJson))
            conn.Run(tx, "INSERT OR IGNORE INTO group_members(device_id, group_id) VALUES ($d, $g)",
                ("$d", deviceId), ("$g", gid));

        tx.Commit();
        Audit.Add(_db, "enroll", "device.enrolled", $"device_id={deviceId} name={deviceName} token_id={tokenId}");
        _log.LogInformation("device enrolled: {DeviceId} name={Name} (secrets not logged)", deviceId, deviceName);

        var trustPin = Db.GetSetting(conn, "server_trust_pin") ?? "";
        return new(201, null, new EnrollResponse(deviceId, secret, trustPin));
    }

    public (int Status, string? Detail) RevokeDevice(string actor, string deviceId)
    {
        using var conn = _db.Open();
        if (conn.Run("UPDATE devices SET revoked = 1, status = 'revoked' WHERE device_id = $d", ("$d", deviceId)) == 0)
            return (404, "device not found");
        Audit.Add(_db, actor, "device.revoke", deviceId);
        _log.LogInformation("device revoked: {DeviceId}", deviceId);
        return (200, null);
    }
}

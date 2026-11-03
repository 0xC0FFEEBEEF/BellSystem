using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record TalkLease(string LeaseId, string Username, string TargetJson, long CreatedAt, long ExpiresAt,
    int RenewIntervalS, long RenewDeadline, string Status, string? EndedReason, string? AnnouncementId)
{
    public int MaxDurationS => (int)((ExpiresAt - CreatedAt) / 1000);
    public string CreatedAtIso => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToString("o");
    public string ExpiresAtIso => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt).ToString("o");
    public TargetScope Target => TalkLeaseService.ReadTarget(TargetJson);
}

public sealed record LeaseOutcome(int Status, string? Detail, TalkLease? Lease = null);

/// Talk lease (PROTOCOL.md §7 / plan §6): one active lease schoolwide for the pilot.
/// expires_at = start + max_duration_s (hard cap; renewal never extends past it) and
/// renew_deadline = last renewal + renew interval (default 60 s). A lease that is not renewed,
/// or whose hard cap is reached, expires; the sweeper then ends any live announcement it held
/// with announcement.end(ended_by: lease).
/// Role and group-scope authorization are enforced here, in the service the API calls, not only
/// in the pages (PROTOCOL.md §10).
public sealed class TalkLeaseService
{
    private readonly Db _db;
    private readonly LiveSettings _settings;
    private readonly DevicesService _devices;
    private readonly TimeProvider _time;
    private readonly ILogger<TalkLeaseService> _log;

    public TalkLeaseService(Db db, LiveSettings settings, DevicesService devices, TimeProvider time,
        ILogger<TalkLeaseService> log)
    {
        _db = db; _settings = settings; _devices = devices; _time = time; _log = log;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public static TargetScope ReadTarget(string json)
    {
        try { return JsonSerializer.Deserialize<TargetScope>(json, ControlEnvelope.Json) ?? new TargetScope(true, null, null); }
        catch (JsonException) { return new TargetScope(true, null, null); }
    }

    public static string WriteTarget(TargetScope target) => JsonSerializer.Serialize(target, ControlEnvelope.Json);

    /// The gate every lease/announce/record call runs first.
    public (int Status, string? Detail) GateTarget(CurrentUser user, TargetScope target)
    {
        if (user.Role is not ("admin" or "operator"))
            return (403, "only administrators and operators can make announcements");
        if (Guard.CanTarget(user, target, _devices.GroupsOf)) return (200, null);
        if (user.Role == "operator" && target.All == true && !user.SchoolwideBroadcast)
            return (403, "operator without schoolwide_broadcast may not target all endpoints");
        return (403, "target is outside your group scope");
    }

    public LeaseOutcome Create(CurrentUser user, TargetScope target, int? maxDurationS)
    {
        var (gate, gateDetail) = GateTarget(user, target);
        if (gate != 200) return new(gate, gateDetail);

        int cap = _settings.LeaseMaxS;                  // settings value, itself clamped to 30..900
        int max = maxDurationS ?? cap;
        if (max < LiveSettings.LeaseMinS)
            return new(400, $"max_duration_s must be at least {LiveSettings.LeaseMinS} s");
        if (max > cap)
            return new(400, $"max_duration_s {max} s exceeds the configured talk_lease_max_s ({cap} s)");

        Sweep();
        var active = ActiveLeaseRaw();
        if (active is not null)
            return new(409, $"lease held by {active.Username} since {active.CreatedAtIso}");

        long now = NowMs;
        int interval = _settings.LeaseRenewIntervalS;
        var leaseId = Guid.NewGuid().ToString();
        using var conn = _db.Open();
        conn.Run("""
            INSERT INTO talk_leases(lease_id, username, target_json, created_at, expires_at, renew_interval_s,
                                    renew_deadline, status)
            VALUES ($l, $u, $t, $c, $e, $i, $r, 'active')
            """,
            ("$l", leaseId), ("$u", user.Username), ("$t", WriteTarget(target)), ("$c", now),
            ("$e", now + max * 1000L), ("$i", interval), ("$r", now + interval * 1000L));
        Audit.Add(conn, null, user.Username, "announcement.lease_create",
            $"lease={leaseId} max_duration_s={max} target={WriteTarget(target)}");
        _log.LogInformation("talk lease {LeaseId} granted to {User}: {Max}s hard cap, renew every {Renew}s",
            leaseId, user.Username, max, interval);
        return new(201, null, Read(conn, leaseId));
    }

    public LeaseOutcome Renew(string leaseId, CurrentUser user, int? extendS)
    {
        var lease = Read(leaseId);
        if (lease is null) return new(404, "no such lease");
        if (lease.Username != user.Username) return new(403, $"lease held by {lease.Username}");
        if (lease.Status != "active") return new(410, $"lease expired ({lease.EndedReason ?? "expired"})");

        long now = NowMs;
        if (now >= lease.ExpiresAt)
        {
            MarkExpired(leaseId, "max_duration");
            return new(410, "lease expired (max duration reached)");
        }
        if (now > lease.RenewDeadline)
        {
            MarkExpired(leaseId, "non_renewal");
            return new(410, "lease expired (not renewed in time)");
        }

        int interval = _settings.LeaseRenewIntervalS;
        if (extendS is not null && extendS.Value <= 0)
            return new(400, "extend_s must be a positive number of seconds");
        long requested = extendS is null ? Math.Min(now + interval * 1000L, lease.ExpiresAt)
                                         : now + (long)extendS.Value * 1000L;
        if (requested > lease.ExpiresAt)
            return new(400, "renew cannot extend the lease past start + max_duration_s (" +
                             DateTimeOffset.FromUnixTimeMilliseconds(lease.ExpiresAt).ToString("o") + ")");

        using var conn = _db.Open();
        conn.Run("UPDATE talk_leases SET renew_deadline = $r WHERE lease_id = $l AND status = 'active'",
            ("$r", Math.Min(requested, lease.ExpiresAt)), ("$l", leaseId));
        return new(200, null, Read(conn, leaseId));
    }

    public LeaseOutcome Release(string leaseId, CurrentUser user)
    {
        var lease = Read(leaseId);
        if (lease is null) return new(404, "no such lease");
        if (lease.Username != user.Username) return new(403, $"lease held by {lease.Username}");
        if (lease.Status == "active")
        {
            using var conn = _db.Open();
            conn.Run("UPDATE talk_leases SET status = 'released', ended_reason = 'released' WHERE lease_id = $l",
                ("$l", leaseId));
            Audit.Add(conn, null, user.Username, "announcement.lease_release", $"lease={leaseId}");
        }
        return new(200, null, Read(leaseId));
    }

    /// Attach the live announcement a lease started (so expiry can end it).
    public void AttachAnnouncement(string leaseId, string announcementId)
    {
        using var conn = _db.Open();
        conn.Run("UPDATE talk_leases SET announcement_id = $a WHERE lease_id = $l",
            ("$a", announcementId), ("$l", leaseId));
    }

    public TalkLease? ActiveLease()
    {
        Sweep();
        return ActiveLeaseRaw();
    }

    private TalkLease? ActiveLeaseRaw()
    {
        using var conn = _db.Open();
        var id = conn.Scalar("SELECT lease_id FROM talk_leases WHERE status = 'active' ORDER BY created_at LIMIT 1") as string;
        return id is null ? null : Read(conn, id);
    }

    public TalkLease? Read(string leaseId)
    {
        using var conn = _db.Open();
        return Read(conn, leaseId);
    }

    private static TalkLease? Read(SqliteConnection conn, string leaseId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lease_id, username, target_json, created_at, expires_at, renew_interval_s, renew_deadline,
                   status, ended_reason, announcement_id
            FROM talk_leases WHERE lease_id = $l
            """;
        cmd.Parameters.AddWithValue("$l", leaseId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new TalkLease(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5),
            r.GetInt64(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9));
    }

    /// Expire overdue leases and return them (with their announcement id when they held a live
    /// session) so the sweeper can end those announcements.
    public List<TalkLease> Sweep()
    {
        long now = NowMs;
        var overdue = new List<(string Id, string Reason)>();
        using var conn = _db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT lease_id, expires_at FROM talk_leases
                WHERE status = 'active' AND (expires_at <= $n OR renew_deadline <= $n)
                """;
            cmd.Parameters.AddWithValue("$n", now);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                overdue.Add((r.GetString(0), r.GetInt64(1) <= now ? "max_duration" : "non_renewal"));
        }

        var expired = new List<TalkLease>();
        foreach (var (id, reason) in overdue)
        {
            MarkExpired(id, reason);
            var full = Read(conn, id);
            if (full is null) continue;
            _log.LogInformation("talk lease {LeaseId} expired ({Reason}): held by {User}, announcement {AnnouncementId}",
                id, reason, full.Username, full.AnnouncementId ?? "-");
            expired.Add(full);
        }
        return expired;
    }

    public void MarkExpired(string leaseId, string reason)
    {
        using var conn = _db.Open();
        conn.Run("UPDATE talk_leases SET status = 'expired', ended_reason = $r WHERE lease_id = $l AND status = 'active'",
            ("$r", reason), ("$l", leaseId));
    }

    /// The office mic socket died: the lease cannot be renewed or trusted any more.
    public void Invalidate(string leaseId, string reason)
    {
        using var conn = _db.Open();
        conn.Run("UPDATE talk_leases SET status = 'expired', ended_reason = $r WHERE lease_id = $l AND status = 'active'",
            ("$r", reason), ("$l", leaseId));
    }

    public object PublicView(TalkLease lease) => new
    {
        lease_id = lease.LeaseId,
        holder = lease.Username,
        created_at = lease.CreatedAt,
        expires_at = lease.ExpiresAt,
        max_duration_s = lease.MaxDurationS,
        renew_interval_s = lease.RenewIntervalS,
        renew_deadline = lease.RenewDeadline,
        status = lease.Status,
        ended_reason = lease.EndedReason,
        announcement_id = lease.AnnouncementId,
    };
}

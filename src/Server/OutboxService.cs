using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// Durable per-device event outbox (plan §8: "durable event outbox").
/// Bounded per device (configurable cap, default 500); oldest entries are dropped with a visible
/// diagnostic — never a silent trim. Live connections receive events as they are produced.
public sealed class OutboxService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly TimeProvider _time;
    private readonly ControlRegistry _registry;
    private readonly ILogger<OutboxService> _log;
    private long _totalDropped;

    public OutboxService(Db db, ServerConfig cfg, TimeProvider time, ControlRegistry registry, ILogger<OutboxService> log)
    {
        _db = db; _cfg = cfg; _time = time; _registry = registry; _log = log;
    }

    public long TotalDropped => Interlocked.Read(ref _totalDropped);

    public (long Seq, ControlEnvelope Envelope) Enqueue(string deviceId, ControlEnvelope env)
    {
        var json = env.ToJson();
        long seq;
        long? droppedBelow = null;
        using (var conn = _db.Open())
        {
            seq = (long)(conn.Scalar("""
                INSERT INTO outbox(device_id, event_id, event_json, created_at) VALUES ($d, $e, $j, $t) RETURNING seq
                """,
                ("$d", deviceId), ("$e", env.EventId), ("$j", json), ("$t", _time.GetUtcNow().ToUnixTimeMilliseconds())) ?? 0);

            // cap: keep newest N per device; drop oldest with a visible diagnostic
            var cutoff = conn.Scalar(
                "SELECT seq FROM outbox WHERE device_id = $d ORDER BY seq DESC LIMIT 1 OFFSET $off",
                ("$d", deviceId), ("$off", _cfg.OutboxCap));
            if (cutoff is not null)
            {
                droppedBelow = Convert.ToInt64(cutoff);
                var removed = conn.Run("DELETE FROM outbox WHERE device_id = $d AND seq <= $c",
                    ("$d", deviceId), ("$c", droppedBelow));
                if (removed > 0)
                {
                    Interlocked.Add(ref _totalDropped, removed);
                    _log.LogWarning("outbox cap {Cap} reached for device {DeviceId}: dropped {Removed} oldest event(s)",
                        _cfg.OutboxCap, deviceId, removed);
                }
            }
        }

        _registry.Lookup(deviceId)?.Send(env);
        return (seq, env);
    }

    /// Replay events with seq strictly after the cursor event. Unknown cursor (dropped from the
    /// bounded outbox) replays everything still stored; devices dedupe at-most-once by event_id.
    public List<string> Replay(string deviceId, string? cursorEventId)
    {
        using var conn = _db.Open();
        long afterSeq = 0;
        if (!string.IsNullOrEmpty(cursorEventId))
        {
            var seqObj = conn.Scalar("SELECT seq FROM outbox WHERE device_id = $d AND event_id = $e",
                ("$d", deviceId), ("$e", cursorEventId));
            if (seqObj is null)
                _log.LogInformation("resync cursor not found in outbox for {DeviceId}; replaying remaining bounded events", deviceId);
            else
                afterSeq = Convert.ToInt64(seqObj);
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event_json FROM outbox WHERE device_id = $d AND seq > $s ORDER BY seq";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$s", afterSeq);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public (long Total, long MaxPerDevice) Depth()
    {
        using var conn = _db.Open();
        var total = Convert.ToInt64(conn.Scalar("SELECT COUNT(*) FROM outbox") ?? 0);
        var max = Convert.ToInt64(conn.Scalar(
            "SELECT COALESCE(MAX(c), 0) FROM (SELECT COUNT(*) AS c FROM outbox GROUP BY device_id)") ?? 0);
        return (total, max);
    }
}

using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// Receipt ingest (idempotent via UNIQUE(device_id, ref_id, state)) and honest delivery reporting.
public sealed class ReceiptsService
{
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly Microsoft.Extensions.Logging.ILogger<ReceiptsService> _log;

    public ReceiptsService(Db db, TimeProvider time, Microsoft.Extensions.Logging.ILogger<ReceiptsService> log)
    {
        _db = db; _time = time; _log = log;
    }

    public int Ingest(string deviceId, string refId, string state, long at, string? errorCode)
    {
        if (string.IsNullOrEmpty(refId) || !Protocol.ReceiptStates.Contains(state))
            return -1; // protocol violation; caller closes connection
        using var conn = _db.Open();
        var n = conn.Run("""
            INSERT OR IGNORE INTO receipts(device_id, ref_id, state, at, error_code, created_at)
            VALUES ($d, $r, $s, $a, $e, $t)
            """,
            ("$d", deviceId), ("$r", refId), ("$s", state), ("$a", at), ("$e", errorCode),
            ("$t", _time.GetUtcNow().ToUnixTimeMilliseconds()));
        if (n > 0) LogReceipt(conn, deviceId, refId, state, errorCode);
        return n;
    }

    /// Server log: what each computer did with each bell or announcement. Anything that did not
    /// play is logged normally; progress and success only with verbose logging.
    private void LogReceipt(Microsoft.Data.Sqlite.SqliteConnection conn, string deviceId, string refId, string state, string? errorCode)
    {
        var level = state is ReceiptState.Completed or ReceiptState.Started or ReceiptState.Ready or ReceiptState.Offered
            ? Microsoft.Extensions.Logging.LogLevel.Debug : Microsoft.Extensions.Logging.LogLevel.Information;
        if (!_log.IsEnabled(level)) return;
        try
        {
            var device = conn.Scalar("""
                SELECT CASE WHEN active_user IS NOT NULL THEN active_user || ' (' || COALESCE(computer_name, name) || ')'
                            ELSE COALESCE(computer_name, name) END FROM devices WHERE device_id = $d
                """, ("$d", deviceId)) as string ?? deviceId;
            string what;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT bell_name, local_date, local_hm FROM occurrences WHERE occurrence_id = $r ORDER BY revision DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$r", refId);
                using var r = cmd.ExecuteReader();
                what = r.Read() ? $"scheduled bell '{r.GetString(0)}' {r.GetString(1)} {r.GetString(2)}"
                    : conn.Scalar("SELECT bell_name FROM bell_rings WHERE ring_id = $r", ("$r", refId)) is string ring
                        ? $"manual bell '{ring}'" : $"announcement {refId}";
            }
            _log.Log(level, "{Device}: {What} -> {State}{Error}", device, what, state,
                errorCode is null ? "" : $" ({errorCode})");
        }
        catch (Exception e) { _log.LogDebug("receipt log lookup failed: {Error}", e.Message); }
    }

    /// pastOnly (History): bells whose time has come, newest first. A bell counts when the latest
    /// publish still has it or an endpoint reported it, so duplicates merged away by a later publish
    /// do not show up as unanswered.
    public List<RecentBell> RecentBells(int limit, long nowMs, bool pastOnly = false)
    {
        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        using var conn = _db.Open();
        var latest = Convert.ToInt64(conn.Scalar("SELECT COALESCE(MAX(revision), 0) FROM published_revisions") ?? 0);

        // Latest revision metadata per stable occurrence id, soonest-in-future-first-then-recent-past:
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.occurrence_id, o.bell_name, o.kind, o.template_name, o.local_date, o.local_hm,
                   o.starts_at, o.volume_policy, o.sound_id
            FROM occurrences o
            JOIN (SELECT occurrence_id, MAX(revision) AS mr FROM occurrences GROUP BY occurrence_id) m
              ON m.occurrence_id = o.occurrence_id AND m.mr = o.revision
            WHERE $past = 0 OR (o.starts_at <= $now AND (o.revision = $latest
                  OR EXISTS (SELECT 1 FROM receipts rc WHERE rc.ref_id = o.occurrence_id)))
            ORDER BY o.starts_at DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$past", pastOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$latest", latest);
        var bells = new List<(string Id, string BellName, string Kind, string Template, string Date, string Hm, long StartsAt)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                bells.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6)));

        if (bells.Count == 0) return [];

        var ids = bells.Select(b => b.Id).ToList();
        var idParams = ids.Select((id, i) => ($"$id{i}", (object)id)).ToArray();
        var inClause = string.Join(",", idParams.Select(p => p.Item1));
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = $"""
            SELECT rc.ref_id, rc.device_id, d.name, d.computer_name, d.active_user, rc.state, rc.at, rc.error_code
            FROM receipts rc JOIN devices d ON d.device_id = rc.device_id
            WHERE rc.ref_id IN ({inClause}) ORDER BY rc.at DESC
            """;
        foreach (var (n, v) in idParams) cmd2.Parameters.AddWithValue(n, v);

        var deliveries = new Dictionary<string, List<DeliveryResult>>();
        using (var r = cmd2.ExecuteReader())
            while (r.Read())
            {
                var refId = r.GetString(0);
                var computer = r.IsDBNull(3) ? r.GetString(2) : r.GetString(3);
                var user = r.IsDBNull(4) ? null : r.GetString(4);
                var label = user is not null ? $"{user} ({computer})" : $"No signed-in user ({computer})";
                deliveries.TryAdd(refId, []);
                deliveries[refId].Add(new DeliveryResult(r.GetString(1), label, r.GetString(5),
                    r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7)));
            }

        return bells.Select(b => new RecentBell(b.Id, b.BellName, b.Kind, b.Template, b.Date, b.Hm,
            b.StartsAt, b.StartsAt <= nowMs, deliveries.TryGetValue(b.Id, out var d) ? d : [],
            deliveries.ContainsKey(b.Id))).ToList();
    }
}

public sealed record DeliveryResult(string DeviceId, string Label, string State, long At, string? ErrorCode);

public sealed record RecentBell(string OccurrenceId, string BellName, string Kind, string ScheduleTemplate,
    string LocalDate, string LocalHm, long StartsAt, bool IsPast, List<DeliveryResult> Delivery, bool HasReceipts);

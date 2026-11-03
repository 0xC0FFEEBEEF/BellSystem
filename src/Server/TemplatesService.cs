using System.Text.Json;
using System.Text.RegularExpressions;
using NodaTime;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record PeriodInput(string Name, string StartHm, string EndHm, int WarnMin,
    string SoundId, string VolumePolicy, int DurationS);

public sealed record TemplateInput(string Name, TargetScope Target, List<PeriodInput> Periods, List<int> Weekdays);

public sealed record TemplateDto(long Id, string Name, TargetScope Target, List<PeriodDto> Periods, List<int> Weekdays);
public sealed record PeriodDto(int PeriodIndex, string Name, string StartHm, string EndHm, int WarnMin,
    string SoundId, string VolumePolicy, int DurationS);

public sealed record Outcome(int Status, string? Detail);

public static class DeviceGroupsLookup
{
    /// Func adapter for Guard.CanTarget device-scope checks.
    public static Func<string, IEnumerable<long>> For(Db db) =>
        deviceId => { using var conn = db.Open(); return DevicesService.GroupsOf(conn, deviceId); };
}

/// Schedule template CRUD with validation, plus calendar overrides and holidays.
public sealed class TemplatesService
{
    private static readonly Regex HmRe = new("^([01][0-9]|2[0-3]):[0-5][0-9]$");
    private static readonly Regex DateRe = new(@"^\d{4}-\d{2}-\d{2}$");

    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly SoundService _sounds;

    public TemplatesService(Db db, TimeProvider time, SoundService sounds)
    {
        _db = db; _time = time; _sounds = sounds;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public List<TemplateDto> ListTemplates()
    {
        using var conn = _db.Open();
        return LoadTemplates(conn);
    }

    public Outcome Create(CurrentUser actor, TemplateInput input)
    {
        var v = Validate(input, null);
        if (v.Status != 0) return v;
        if (!Guard.CanTarget(actor, input.Target, DeviceGroupsLookup.For(_db)))
            return new(403, "target outside your group scope");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var id = (int)ScalarWithTx(conn, tx, """
            INSERT INTO schedule_templates(name, target, created_at, updated_at)
            VALUES ($n, $t, $c, $u) RETURNING id
            """, ("$n", input.Name), ("$t", JsonSerializer.Serialize(input.Target, ControlEnvelope.Json)),
            ("$c", NowMs), ("$u", NowMs));
        InsertPeriods(conn, tx, id, input);
        InsertWeekdays(conn, tx, id, input.Weekdays);
        tx.Commit();
        Audit.Add(_db, actor.Username, "schedule.template.create", $"id={id} name={input.Name}");
        return new(201, null);
    }

    public Outcome Update(CurrentUser actor, long id, TemplateInput input)
    {
        var v = Validate(input, id);
        if (v.Status != 0) return v;
        var existing = GetTemplateTarget(id);
        if (existing is null) return new(404, "template not found");
        if (!Guard.CanTarget(actor, existing, DeviceGroupsLookup.For(_db)) ||
            !Guard.CanTarget(actor, input.Target, DeviceGroupsLookup.For(_db)))
            return new(403, "template target outside your group scope");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        conn.Run(tx, "UPDATE schedule_templates SET name = $n, target = $t, updated_at = $u WHERE id = $id",
            ("$n", input.Name), ("$t", JsonSerializer.Serialize(input.Target, ControlEnvelope.Json)),
            ("$u", NowMs), ("$id", id));
        conn.Run(tx, "DELETE FROM template_periods WHERE template_id = $id", ("$id", id));
        conn.Run(tx, "DELETE FROM weekday_assignments WHERE template_id = $id", ("$id", id));
        InsertPeriods(conn, tx, (int)id, input);
        InsertWeekdays(conn, tx, (int)id, input.Weekdays);
        tx.Commit();
        Audit.Add(_db, actor.Username, "schedule.template.update", $"id={id}");
        return new(200, null);
    }

    public Outcome Delete(CurrentUser actor, long id)
    {
        using var conn = _db.Open();
        if (conn.Run("DELETE FROM schedule_templates WHERE id = $id", ("$id", id)) == 0)
            return new(404, "template not found");
        Audit.Add(_db, actor.Username, "schedule.template.delete", $"id={id}");
        return new(200, null);
    }

    public Outcome UpsertOverride(CurrentUser actor, string date, long? templateId)
    {
        if (!DateRe.IsMatch(date)) return new(400, "date must be YYYY-MM-DD");
        using var conn = _db.Open();
        if (templateId is not null &&
            !conn.Exists("SELECT COUNT(*) FROM schedule_templates WHERE id = $t", ("$t", templateId.Value)))
            return new(400, "template_id does not exist");
        conn.Run("""
            INSERT INTO date_overrides(date, template_id) VALUES ($d, $t)
            ON CONFLICT(date) DO UPDATE SET template_id = $t
            """, ("$d", date), ("$t", templateId));
        Audit.Add(_db, actor.Username, "calendar.override.upsert",
            $"date={date} template={(templateId is null ? "none(no bells)" : templateId)}");
        return new(200, null);
    }

    public Outcome DeleteOverride(CurrentUser actor, string date)
    {
        using var conn = _db.Open();
        if (conn.Run("DELETE FROM date_overrides WHERE date = $d", ("$d", date)) == 0)
            return new(404, "override not found");
        Audit.Add(_db, actor.Username, "calendar.override.delete", $"date={date}");
        return new(200, null);
    }

    public Outcome AddHoliday(CurrentUser actor, string date, string name)
    {
        if (!DateRe.IsMatch(date)) return new(400, "date must be YYYY-MM-DD");
        if (string.IsNullOrWhiteSpace(name)) return new(400, "holiday name is required");
        using var conn = _db.Open();
        conn.Run("""
            INSERT INTO holidays(date, name) VALUES ($d, $n)
            ON CONFLICT(date) DO UPDATE SET name = $n
            """, ("$d", date), ("$n", name));
        Audit.Add(_db, actor.Username, "calendar.holiday.add", $"date={date} name={name}");
        return new(200, null);
    }

    public Outcome DeleteHoliday(CurrentUser actor, string date)
    {
        using var conn = _db.Open();
        if (conn.Run("DELETE FROM holidays WHERE date = $d", ("$d", date)) == 0)
            return new(404, "holiday not found");
        Audit.Add(_db, actor.Username, "calendar.holiday.delete", $"date={date}");
        return new(200, null);
    }

    public List<TemplateDto> LoadTemplates(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var list = new List<TemplateDto>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, target FROM schedule_templates ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            list.Add(new TemplateDto(id, r.GetString(1), ParseTarget(r.GetString(2)), [], []));
        }
        r.Close();
        foreach (var t in list.ToList())
        {
            var periods = new List<PeriodDto>();
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = """
                    SELECT period_index, name, start_hm, end_hm, warn_min, sound_id, volume_policy, duration_s
                    FROM template_periods WHERE template_id = $t ORDER BY period_index
                    """;
                cmd2.Parameters.AddWithValue("$t", t.Id);
                using var r2 = cmd2.ExecuteReader();
                while (r2.Read())
                    periods.Add(new PeriodDto(r2.GetInt32(0), r2.GetString(1), r2.GetString(2), r2.GetString(3),
                        r2.GetInt32(4), r2.GetString(5), r2.GetString(6), r2.GetInt32(7)));
            }
            var days = new List<int>();
            using (var cmd3 = conn.CreateCommand())
            {
                cmd3.CommandText = "SELECT day FROM weekday_assignments WHERE template_id = $t ORDER BY day";
                cmd3.Parameters.AddWithValue("$t", t.Id);
                using var r3 = cmd3.ExecuteReader();
                while (r3.Read()) days.Add(r3.GetInt32(0));
            }
            list[list.FindIndex(x => x.Id == t.Id)] = t with { Periods = periods, Weekdays = days };
        }
        return list;
    }

    public List<CompileTemplate> LoadCompileTemplates()
    {
        using var conn = _db.Open();
        return LoadTemplates(conn).Select(t => new CompileTemplate(t.Id, t.Name, t.Target,
            t.Periods.Select(p => new CompilePeriod(p.PeriodIndex, p.Name, p.StartHm, p.EndHm,
                p.WarnMin, p.SoundId, p.VolumePolicy, p.DurationS)).ToList(),
            new SortedSet<int>(t.Weekdays))).ToList();
    }

    public Dictionary<string, long?> LoadOverrides()
    {
        using var conn = _db.Open();
        var map = new Dictionary<string, long?>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date, template_id FROM date_overrides";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetInt64(1);
        return map;
    }

    public Dictionary<string, string> LoadHolidays()
    {
        using var conn = _db.Open();
        var map = new Dictionary<string, string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date, name FROM holidays";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public Outcome Preview(string tzId, string fromStr, string toStr, out CompileResult? preview)
    {
        preview = null;
        if (!DateRe.IsMatch(fromStr) || !DateRe.IsMatch(toStr)) return new(400, "from and to must be YYYY-MM-DD");
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tzId)
            ?? throw new InvalidOperationException("timezone not configured");
        LocalDate from, to;
        try { from = LocalDate.FromDateTime(DateTime.Parse(fromStr)); to = LocalDate.FromDateTime(DateTime.Parse(toStr)); }
        catch { return new(400, "from and to must be valid dates"); }
        if (to < from) return new(400, "to must be on or after from");
        if (PeriodDays(from, to) > 31) return new(400, "preview range is limited to 31 days");

        var horizonEnd = to.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();
        var expiresMs = ScheduleCompiler.ToUnixMs(horizonEnd) + 3600_000;
        preview = ScheduleCompiler.Compile(zone, from, to, LoadCompileTemplates(), LoadOverrides(), LoadHolidays(), expiresMs);
        return new(200, null);
    }

    public static int PeriodDays(LocalDate from, LocalDate to) =>
        new DateInterval(from, to).Length;

    private TargetScope? GetTemplateTarget(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT target FROM schedule_templates WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : ParseTarget(json);
    }

    private static TargetScope ParseTarget(string json)
    {
        try { return JsonSerializer.Deserialize<TargetScope>(json, ControlEnvelope.Json) ?? new TargetScope(true, null, null); }
        catch (JsonException) { return new TargetScope(true, null, null); }
    }

    private Outcome Validate(TemplateInput input, long? selfId)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return new(400, "template name is required");
        if (input.Name.Length > 100) return new(400, "template name is too long (max 100)");
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM schedule_templates WHERE name = $n";
            cmd.Parameters.AddWithValue("$n", input.Name);
            var existingId = cmd.ExecuteScalar() as long?;
            if (existingId is not null && existingId != selfId)
                return new(400, "template name must be unique");
        }

        if (input.Target is null) return new(400, "target is required");
        var hasAll = input.Target.All == true;
        var hasGroups = input.Target.Groups is { Count: > 0 };
        var hasDevices = input.Target.Devices is { Count: > 0 };
        if (new[] { hasAll, hasGroups, hasDevices }.Count(x => x) != 1)
            return new(400, "target must set exactly one of all/groups/devices");
        if (hasGroups)
            using (var conn = _db.Open())
                foreach (var g in input.Target.Groups!)
                    if (!conn.Exists("SELECT COUNT(*) FROM groups WHERE id = $g", ("$g", g)))
                        return new(400, $"unknown group id {g}");

        if (input.Periods is null || input.Periods.Count == 0)
            return new(400, "at least one period is required");
        foreach (var p in input.Periods)
        {
            if (!HmRe.IsMatch(p.StartHm) || !HmRe.IsMatch(p.EndHm))
                return new(400, $"period '{p.Name}': times must be HH:MM (24-hour)");
            if (ScheduleCompiler.HmToMinutes(p.EndHm) <= ScheduleCompiler.HmToMinutes(p.StartHm))
                return new(400, $"period '{p.Name}': end must be after start");
            if (p.WarnMin is < 0 or > 240)
                return new(400, $"period '{p.Name}': warn_min must be 0-240");
            if (p.DurationS is < 1 or > 120)
                return new(400, $"period '{p.Name}': duration_s must be 1-120");
            if (p.VolumePolicy is not ("announcement" or "inherit"))
                return new(400, $"period '{p.Name}': volume_policy must be announcement or inherit");
            if (!_sounds.Exists(p.SoundId))
                return new(400, $"period '{p.Name}': unknown sound_id '{p.SoundId}'");
            if (string.IsNullOrWhiteSpace(p.Name))
                return new(400, "every period needs a bell name");
        }

        if (input.Weekdays is null) return new(400, "weekdays list is required (may be empty for override-only templates)");
        if (input.Weekdays.Any(d => d is < 0 or > 6))
            return new(400, "weekday days must be 0 (Sunday) through 6 (Saturday)");
        if (input.Weekdays.Count != input.Weekdays.Distinct().Count())
            return new(400, "weekday days must not repeat");
        return new(0, null);
    }

    private void InsertPeriods(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx,
        int templateId, TemplateInput input)
    {
        for (int i = 0; i < input.Periods.Count; i++)
        {
            var p = input.Periods[i];
            conn.Run(tx, """
                INSERT INTO template_periods(template_id, period_index, name, start_hm, end_hm, warn_min, sound_id, volume_policy, duration_s)
                VALUES ($t, $i, $n, $s, $e, $w, $so, $vp, $du)
                """,
                ("$t", templateId), ("$i", i), ("$n", p.Name), ("$s", p.StartHm), ("$e", p.EndHm),
                ("$w", p.WarnMin), ("$so", p.SoundId), ("$vp", p.VolumePolicy), ("$du", p.DurationS));
        }
    }

    private void InsertWeekdays(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx,
        int templateId, List<int> days)
    {
        foreach (var d in days)
            conn.Run(tx, "INSERT INTO weekday_assignments(template_id, day) VALUES ($t, $d)",
                ("$t", templateId), ("$d", d));
    }

    // scalar-with-transaction helper
    private static long ScalarWithTx(Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx, string sql, params (string, object?)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}

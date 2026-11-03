using NodaTime;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record CompilePeriod(int PeriodIndex, string Name, string StartHm, string EndHm,
    int WarnMin, string SoundId, string VolumePolicy, int DurationS);

public sealed record CompileTemplate(long Id, string Name, TargetScope Target,
    List<CompilePeriod> Periods, SortedSet<int> Weekdays);

public sealed record CalendarSkipped(string TemplateName, string BellName, string Kind,
    string LocalDate, string LocalHm, string Reason);

public sealed class CompileReport
{
    public List<string> HolidayDates { get; } = [];
    public List<string> NoBellDates { get; } = [];
    public List<string> NoAssignmentDates { get; } = [];
    public List<CalendarSkipped> SkippedDst { get; } = [];
    public List<CalendarSkipped> AmbiguousFirstMapping { get; } = [];
    public List<CalendarSkipped> WarnBeforeMidnight { get; } = [];
}

public sealed record CompileResult(List<Occurrence> Occurrences, CompileReport Report);

/// Compiles local calendar rules to absolute UTC occurrences (plan §8):
/// holiday wins, then date override (null = no-bells), then weekday assignments.
/// DST: nonexistent local time -> skipped; ambiguous -> FIRST (earlier) mapping only.
public static class ScheduleCompiler
{
    public static CompileResult Compile(DateTimeZone tz, LocalDate from, LocalDate to,
        IReadOnlyList<CompileTemplate> templates, Dictionary<string, long?> overrides,
        Dictionary<string, string> holidays, long expiresAtMs)
    {
        var result = new CompileResult([], new CompileReport());
        for (var date = from; date <= to; date = date.PlusDays(1))
        {
            var dateStr = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            if (holidays.ContainsKey(dateStr))
            {
                result.Report.HolidayDates.Add(dateStr);
                continue;
            }
            List<CompileTemplate> dayTemplates;
            if (overrides.TryGetValue(dateStr, out var overrideTpl))
            {
                if (overrideTpl is null)
                {
                    result.Report.NoBellDates.Add(dateStr);
                    continue;
                }
                var t = templates.FirstOrDefault(x => x.Id == overrideTpl.Value);
                if (t is null) { result.Report.NoAssignmentDates.Add(dateStr); continue; }
                dayTemplates = [t];
            }
            else
            {
                // NodaTime DayOfWeek: Monday=1..Sunday=7; schema day: 0=Sunday..6=Saturday
                int dow = (int)date.DayOfWeek % 7;
                dayTemplates = templates.Where(t => t.Weekdays.Contains(dow))
                    .OrderBy(t => t.Id).ToList();
                if (dayTemplates.Count == 0) { result.Report.NoAssignmentDates.Add(dateStr); continue; }
            }

            foreach (var tpl in dayTemplates)
                foreach (var p in tpl.Periods.OrderBy(p => p.PeriodIndex))
                {
                    var targetKey = OccurrenceIds.TargetKey(tpl.Target);
                    AddOccurrence(result, tz, date, dateStr, tpl, p, p.StartHm, "start", targetKey, expiresAtMs);
                    AddOccurrence(result, tz, date, dateStr, tpl, p, p.EndHm, "end", targetKey, expiresAtMs);
                    if (p.WarnMin > 0)
                    {
                        var startMin = HmToMinutes(p.StartHm);
                        var warnMin = startMin - p.WarnMin;
                        if (warnMin < 0)
                        {
                            result.Report.WarnBeforeMidnight.Add(new CalendarSkipped(
                                tpl.Name, p.Name, "warn", dateStr, p.StartHm, "warn_before_midnight"));
                            continue;
                        }
                        var warnHm = MinutesToHm(warnMin);
                        AddOccurrence(result, tz, date, dateStr, tpl, p, warnHm, "warn", targetKey, expiresAtMs);
                    }
                }
        }
        return result;
    }

    private static void AddOccurrence(CompileResult result, DateTimeZone tz, LocalDate date, string dateStr,
        CompileTemplate tpl, CompilePeriod p, string hm, string kind, string targetKey, long expiresAtMs)
    {
        var local = date.At(HmToLocalTime(hm));
        Instant inst;
        try
        {
            var z = local.InZoneStrictly(tz);
            inst = z.ToInstant();
        }
        catch (SkippedTimeException)
        {
            result.Report.SkippedDst.Add(new CalendarSkipped(tpl.Name, p.Name, kind, dateStr, hm, "dst_nonexistent"));
            return;
        }
        catch (AmbiguousTimeException ex)
        {
            inst = ex.EarlierMapping.ToInstant(); // first interpretation only
            result.Report.AmbiguousFirstMapping.Add(new CalendarSkipped(
                tpl.Name, p.Name, kind, dateStr, hm, "dst_ambiguous_first_mapping"));
        }

        var id = OccurrenceIds.Compute(tpl.Id, p.PeriodIndex, targetKey, dateStr, kind);
        result.Occurrences.Add(new Occurrence(
            id, ToUnixMs(inst), p.Name, kind, p.SoundId,
            p.VolumePolicy, p.DurationS, tpl.Target, tpl.Name, dateStr, hm, expiresAtMs));
    }

    public static long ToUnixMs(Instant inst) => new DateTimeOffset(inst.ToDateTimeUtc()).ToUnixTimeMilliseconds();

    public static int HmToMinutes(string hm)
    {
        var parts = hm.Split(':');
        return int.Parse(parts[0]) * 60 + int.Parse(parts[1]);
    }

    public static LocalTime HmToLocalTime(string hm)
    {
        var parts = hm.Split(':');
        return new LocalTime(int.Parse(parts[0]), int.Parse(parts[1]));
    }

    public static string MinutesToHm(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}

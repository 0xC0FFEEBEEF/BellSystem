using SchoolBell.Contracts;

namespace SchoolBell.Server;

internal sealed record TemplateBody(string? Name, TargetScope? Target, List<PeriodInput>? Periods, List<int>? Weekdays);
internal sealed record OverrideBody(string? Date, long? TemplateId);
internal sealed record HolidayBody(string? Date, string? Name);

public static class ScheduleEndpoints
{
    public static void Map(WebApplication app)
    {
        var schedules = app.Services.GetRequiredService<TemplatesService>();
        var publish = app.Services.GetRequiredService<PublishService>();
        var devices = app.Services.GetRequiredService<DevicesService>();

        static (CurrentUser? User, IResult? Error) WriteAccess(HttpContext ctx)
        {
            if (Guard.User(ctx) is not CurrentUser u)
                return (null, Err.Detail(401, "authentication required"));
            if (u.Role is not ("admin" or "operator"))
                return (null, Err.Detail(403, "role viewer cannot modify schedules"));
            return (u, null);
        }

        app.MapGet("/api/schedules", (HttpContext ctx) =>
            Guard.User(ctx) is null ? Err.Detail(401, "authentication required") : Http.Json(schedules.ListTemplates()));

        app.MapGet("/api/schedules/draft", (HttpContext ctx) =>
        {
            if (Guard.User(ctx) is null) return Err.Detail(401, "authentication required");
            return Http.Json(new
            {
                templates = schedules.ListTemplates(),
                overrides = schedules.LoadOverrides().Select(kv => new { date = kv.Key, template_id = kv.Value }),
                holidays = schedules.LoadHolidays().Select(kv => new { date = kv.Key, name = kv.Value }),
            });
        });

        app.MapPost("/api/schedules", async (HttpContext ctx) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<TemplateBody>(ctx);
            if (body?.Target is null || body.Periods is null)
                return Err.Detail(400, "name, target and periods are required");
            var res = schedules.Create(user, new TemplateInput(body.Name ?? "", body.Target, body.Periods, body.Weekdays ?? []));
            return res.Status == 201 ? Http.Json(new { ok = true }, 201) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapPut("/api/schedules/{id:long}", async (HttpContext ctx, long id) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<TemplateBody>(ctx);
            if (body?.Target is null || body.Periods is null)
                return Err.Detail(400, "name, target and periods are required");
            var res = schedules.Update(user, id, new TemplateInput(body.Name ?? "", body.Target, body.Periods, body.Weekdays ?? []));
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapDelete("/api/schedules/{id:long}", (HttpContext ctx, long id) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            var res = schedules.Delete(user, id);
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapPost("/api/calendar/overrides", async (HttpContext ctx) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            if (user.Role == "operator" && !user.SchoolwideBroadcast)
                return Err.Detail(403, "calendar edits require admin or the schoolwide_broadcast permission");
            var body = await Http.ReadBody<OverrideBody>(ctx);
            if (body?.Date is null) return Err.Detail(400, "date is required");
            var res = schedules.UpsertOverride(user, body.Date, body.TemplateId);
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapDelete("/api/calendar/overrides/{date}", (HttpContext ctx, string date) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            if (user.Role == "operator" && !user.SchoolwideBroadcast)
                return Err.Detail(403, "calendar edits require admin or the schoolwide_broadcast permission");
            var res = schedules.DeleteOverride(user, date);
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapPost("/api/calendar/holidays", async (HttpContext ctx) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            if (user.Role == "operator" && !user.SchoolwideBroadcast)
                return Err.Detail(403, "calendar edits require admin or the schoolwide_broadcast permission");
            var body = await Http.ReadBody<HolidayBody>(ctx);
            if (body?.Date is null || body.Name is null) return Err.Detail(400, "date and name are required");
            var res = schedules.AddHoliday(user, body.Date, body.Name);
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapDelete("/api/calendar/holidays/{date}", (HttpContext ctx, string date) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            if (user.Role == "operator" && !user.SchoolwideBroadcast)
                return Err.Detail(403, "calendar edits require admin or the schoolwide_broadcast permission");
            var res = schedules.DeleteHoliday(user, date);
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapGet("/api/schedules/preview", (HttpContext ctx, string from, string to) =>
        {
            if (Guard.User(ctx) is null) return Err.Detail(401, "authentication required");
            var tz = publish.SchoolTz;
            if (string.IsNullOrEmpty(tz)) return Err.Detail(409, "timezone not configured");
            var res = schedules.Preview(tz, from ?? "", to ?? "", out var preview);
            if (preview is null) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(new
            {
                school_tz = tz,
                from,
                to,
                occurrence_count = preview.Occurrences.Count,
                occurrences = preview.Occurrences.Select(o => PayloadSerializer.Node(o)),
                report = preview.Report,
            });
        });

        app.MapPost("/api/schedules/publish", (HttpContext ctx) =>
        {
            var (user, err) = WriteAccess(ctx);
            if (user is null) return err!;
            if (user.Role == "operator")
            {
                var lookup = DeviceGroupsLookup.For(app.Services.GetRequiredService<Db>());
                foreach (var t in schedules.LoadCompileTemplates())
                    if (!Guard.CanTarget(user, t.Target, lookup))
                        return Err.Detail(403, $"template '{t.Name}' targets outside your group scope");
            }
            var res = publish.Publish(user.Username);
            if (res.Result is null) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(new
            {
                revision = res.Result.Revision,
                horizon_start = res.Result.HorizonStart,
                horizon_end = res.Result.HorizonEnd,
                expires_at = res.Result.ExpiresAt,
                occurrence_count = res.Result.OccurrenceCount,
                devices_notified = res.Result.DevicesNotified,
                report = res.Result.Report,
            });
        });

        app.MapGet("/api/schedules/acceptance", (HttpContext ctx) =>
        {
            if (Guard.User(ctx) is null) return Err.Detail(401, "authentication required");
            var latest = publish.LatestRevision() ?? 0;
            var manifestBytes = publish.LatestManifestBytes();
            var limits = devices.MessageLimits();
            var rows = devices.AllDeviceRevisions().Select(d =>
            {
                var row = devices.Get(d.DeviceId);
                limits.TryGetValue(d.DeviceId, out var lim);
                bool stale = d.AcceptedRevision < latest;
                return new
                {
                    device_id = d.DeviceId,
                    label = row?.Label ?? d.DeviceId,
                    accepted_revision = d.AcceptedRevision,
                    cache_revision = row?.CacheRevision ?? 0,
                    cache_expires_at = row?.CacheExpiresAt,
                    stale,
                    client_version = lim.Version,
                    // this computer's client cannot receive a schedule this large: it needs a client update
                    needs_client_update = stale && manifestBytes > (lim.MaxBytes ?? Protocol.MaxControlMessageBytes),
                };
            });
            return Http.Json(new { latest_revision = latest, devices = rows });
        });

        // Tradeoff (documented for S1): manifest-referenced sound bytes are served by asset id
        // without office auth so offline/enrolled devices can fetch them; integrity is verified
        // against the manifest sha256 (optional ?sha= must match). Announcement media (S2) will
        // use its own bound tokens.
        app.MapGet("/api/sounds/{id}", (HttpContext ctx, string id, string? sha) =>
        {
            var sounds = app.Services.GetRequiredService<SoundService>();
            var resolved = sounds.ResolveFile(id);
            if (resolved is null) return Err.Detail(404, "sound not found");
            if (sha is not null && !Util.HexEqualsConstantTime(sha.ToLowerInvariant(), resolved.Value.Asset.Sha256))
                return Err.Detail(400, "sha256 does not match the stored asset");
            return Results.File(resolved.Value.Path, "audio/wav", fileDownloadName: $"{id}.wav");
        });
    }
}

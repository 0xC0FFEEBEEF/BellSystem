using System.Text.Json;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

internal sealed record LeaseRequest(int? MaxDurationS, TargetScope? Target);
internal sealed record RenewRequest(int? ExtendS);
internal sealed record LiveRequest(TargetScope? Target, bool? Chime, string? LeaseId);
internal sealed record LiveEndRequest(string? AnnouncementId, string? LeaseId);
internal sealed record CorrectRequest(string? Text);

public static class AnnouncementsEndpoints
{
    public static void Map(WebApplication app)
    {
        var leases = app.Services.GetRequiredService<TalkLeaseService>();
        var announcements = app.Services.GetRequiredService<AnnouncementsService>();
        var devicesService = app.Services.GetRequiredService<DevicesService>();

        static (CurrentUser? User, IResult? Error) Office(HttpContext ctx, bool read = false)
        {
            if (Guard.User(ctx) is not CurrentUser u) return (null, Err.Detail(401, "authentication required"));
            if (!read && u.Role is not ("admin" or "operator"))
                return (null, Err.Detail(403, "only administrators and operators can make announcements"));
            return (u, null);
        }

        /// The S1 build left an honest 501 stub on POST /api/announcements/lease (frozen S1 test).
        /// The S2 lease API is a JSON-body API, so a POST with no JSON body at all is genuinely
        /// not implemented and keeps the 501 with a pointer to the real request shape.
        static bool HasJsonBody(HttpContext ctx) =>
            ctx.Request.ContentType is not null && ctx.Request.ContentType.Contains("json") &&
            ((ctx.Request.ContentLength ?? 0) > 0 ||
             (ctx.Request.ContentLength is null && ctx.Request.Headers.TransferEncoding.ToString().Contains("chunked")));

        // ---------------- talk lease ----------------

        app.MapPost("/api/announcements/lease", async (HttpContext ctx) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            if (!HasJsonBody(ctx))
                return Err.Detail(501, "POST api/announcements/lease with no JSON body is not implemented " +
                                       "(milestone S2 lease requests send {max_duration_s?, target}; see PROTOCOL.md §7)");
            var body = await Http.ReadBody<LeaseRequest>(ctx);
            if (body is null) return Err.Detail(400, "request body must be a JSON object");
            var target = body.Target ?? new TargetScope(true, null, null);
            var res = leases.Create(user, target, body.MaxDurationS);
            if (res.Lease is null) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(leaseJson(leases, res.Lease), 201);
        });

        app.MapPost("/api/announcements/lease/{leaseId}/renew", async (HttpContext ctx, string leaseId) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            RenewRequest? body = null;
            if (HasJsonBody(ctx))
            {
                body = await Http.ReadBody<RenewRequest>(ctx);
                if (body is null) return Err.Detail(400, "request body must be a JSON object");
            }
            var res = leases.Renew(leaseId, user, body?.ExtendS);
            if (res.Lease is null) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(leaseJson(leases, res.Lease));
        });

        app.MapGet("/api/announcements/lease/active", (HttpContext ctx) =>
        {
            var (user, err) = Office(ctx, read: true);
            if (user is null) return err!;
            var lease = leases.ActiveLease();
            return Results.Json(new { lease = lease is null ? null : leaseJson(leases, lease) }, Util.ApiJsonVisibleNulls);
        });

        app.MapDelete("/api/announcements/lease/{leaseId}", (HttpContext ctx, string leaseId) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            var lease = leases.Read(leaseId);
            var res = leases.Release(leaseId, user);
            if (res.Lease is null) return Err.Detail(res.Status, res.Detail!);
            // Releasing the lease ends any live announcement it held (announcement.end, ended_by: operator).
            if (res.Lease.AnnouncementId is not null) announcements.End(res.Lease.AnnouncementId, "operator", user.Username);
            return Http.Json(new { ok = true, lease = leaseJson(leases, res.Lease) });
        });

        // ---------------- live session ----------------

        app.MapPost("/api/announcements/live", async (HttpContext ctx) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<LiveRequest>(ctx);
            if (body?.Target is null) return Err.Detail(400, "target is required");
            var res = announcements.StartLive(user, body.Target, body.Chime == true, body.LeaseId);
            if (res.Announcement is null) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(new
            {
                announcement_id = res.Announcement.AnnouncementId,
                kind = res.Announcement.Kind,
                audience_snapshot_id = res.Announcement.AudienceSnapshotId,
                target_label = res.Announcement.TargetLabel,
                device_count = res.DeviceCount,
                offer_expires_at = res.Announcement.OfferExpiresAt,
                window_closes_at = res.Announcement.WindowClosesAt,
                max_duration_s = res.Announcement.MaxDurationS,
                chime = res.Announcement.Chime,
                lease_id = res.Announcement.LeaseId,
            }, 201);
        });

        app.MapPost("/api/announcements/live/end", (HttpContext ctx, string? announcement_id) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            var (annId, leaseId, error) = ResolveLiveTarget(announcements, leases, user, announcement_id);
            if (error is not null) return error;
            // End closes the office mic socket; the mic handler then finalizes the recording and
            // its own end attempt is a no-op (End is idempotent), so ended_by stays "operator".
            announcements.End(annId!, "operator", user.Username);
            return Http.Json(new { ok = true, announcement_id = annId, ended_by = "operator" });
        });

        // ---------------- recorded (record-then-send) ----------------

        app.MapPost("/api/announcements/recorded", async (HttpContext ctx) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            if (!ctx.Request.HasFormContentType)
                return Err.Detail(400, "multipart/form-data with a 'file' field and a 'target' field is required");

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Err.Detail(400, "a WAV 'file' field is required");
            if (file.Length > LiveSettings.RecordedMaxBytes)
                return Err.Detail(400, $"recording is {file.Length} bytes; the limit is {LiveSettings.RecordedMaxBytes} bytes (25 MB)");

            var targetJson = form["target"].ToString();
            if (string.IsNullOrEmpty(targetJson)) return Err.Detail(400, "a 'target' field is required (example: {\"all\":true})");
            TargetScope? target;
            try { target = JsonSerializer.Deserialize<TargetScope>(targetJson, ControlEnvelope.Json); }
            catch (JsonException) { return Err.Detail(400, "'target' must be valid JSON (see PROTOCOL.md §5 target_scope)"); }
            if (target is null) return Err.Detail(400, "'target' must be valid JSON");

            var bytes = new byte[file.Length];
            await using (var s = file.OpenReadStream())
            {
                int off = 0;
                while (off < bytes.Length)
                {
                    int n = await s.ReadAsync(bytes.AsMemory(off, bytes.Length - off), ctx.RequestAborted);
                    if (n <= 0) break;
                    off += n;
                }
            }
            bool chime = form["chime"].ToString() is "1" or "true";

            var res = announcements.StartRecorded(user, target, chime, bytes);
            if (res.Announcement is null) return Err.Detail(res.Status, res.Detail!);
            var a = res.Announcement;
            return Http.Json(new
            {
                announcement_id = a.AnnouncementId,
                kind = a.Kind,
                audience_snapshot_id = a.AudienceSnapshotId,
                target_label = a.TargetLabel,
                device_count = res.DeviceCount,
                offer_expires_at = a.OfferExpiresAt,
                max_duration_s = a.MaxDurationS,
                chime = a.Chime,
                sha256 = a.RecordingSha256,
                bytes = a.RecordingBytes,
                duration_ms = a.RecordingDurationMs,
                sample_rate = a.RecordingSampleRate,
                audio_url = $"api/announcements/{a.AnnouncementId}/audio",
            }, 201);
        });

        // ---------------- active / history / transcripts ----------------

        app.MapGet("/api/announcements/active", (HttpContext ctx) =>
        {
            var (user, err) = Office(ctx, read: true);
            if (user is null) return err!;
            return Results.Json(announcements.ActiveSummary(user), Util.ApiJsonVisibleNulls);
        });

        app.MapGet("/api/announcements/{announcementId}", (HttpContext ctx, string announcementId) =>
        {
            var (user, err) = Office(ctx, read: true);
            if (user is null) return err!;
            var view = announcements.View(announcementId);
            if (view is null) return Err.Detail(404, "no such announcement");
            var a = view.Announcement;
            var t = announcements.Transcript(announcementId);
            return Results.Json(new
            {
                announcement_id = a.AnnouncementId,
                kind = a.Kind,
                status = a.Status,
                created_by = a.CreatedBy,
                source_label = a.SourceLabel,
                target_label = a.TargetLabel,
                target = a.Target,
                audience_snapshot_id = a.AudienceSnapshotId,
                lease_id = a.LeaseId,
                created_at = a.CreatedAt,
                started_at = a.StartedAt,
                ended_at = a.EndedAt,
                ended_by = a.EndedBy,
                max_duration_s = a.MaxDurationS,
                offer_expires_at = a.OfferExpiresAt,
                chime = a.Chime,
                frames_sent = a.FramesSent,
                targeted = view.Targeted,
                final_counts = view.FinalCounts,
                audible_completed = view.AudibleCompleted,
                socket_delivered = view.SocketDelivered,
                delivery = view.Devices.Select(d => new
                {
                    device_id = d.DeviceId,
                    label = d.Label,
                    state = d.State,
                    error_code = d.ErrorCode,
                    source = d.Source,
                    at = d.At,
                    states = d.AllStates,
                }),
                recording = view.RecordingView(),
                transcript = t is null ? null : new
                {
                    status = t.Status,
                    transcript_original = t.Original,
                    transcript_current = t.Current,
                    engine = t.Engine,
                    corrected_by = t.CorrectedBy,
                    corrected_at = t.CorrectedAt,
                    purged = t.Purged,
                },
            }, Util.ApiJsonVisibleNulls);
        });

        // Stored announcement audio, when the caller is authorized for it (plan §11).
        app.MapGet("/api/announcements/{announcementId}/audio", (HttpContext ctx, string announcementId) =>
        {
            var (user, err) = Office(ctx, read: true);
            if (user is null) return err!;
            if (user.Role == "viewer") return Err.Detail(403, "viewers may read history and diagnostics, not announcement audio");
            var a = announcements.Read(announcementId);
            if (a is null) return Err.Detail(404, "no such announcement");
            if (user.Role == "operator" && !Guard.CanTarget(user, a.Target, devicesService.GroupsOf))
                return Err.Detail(403, "this announcement is outside your group scope");
            if (a.RecordingPath is null || !File.Exists(a.RecordingPath))
                return Err.Detail(404, "no recording is stored for this announcement (or it was deleted by retention)");
            return Results.File(a.RecordingPath, "audio/wav", fileDownloadName: Path.GetFileName(a.RecordingPath));
        });

        app.MapGet("/api/history", (HttpContext ctx, string? kind, long? from, long? to, string? device, int limit = 50) =>
        {
            var (user, err) = Office(ctx, read: true);
            if (user is null) return err!;
            var views = announcements.History(user, kind, from, to, device, limit);
            return Http.Json(new
            {
                items = views.Select(v =>
                {
                    var a = v.Announcement;
                    var transcript = announcements.Transcript(a.AnnouncementId);
                    return new
                    {
                        announcement_id = a.AnnouncementId,
                        kind = a.Kind,
                        status = a.Status,
                        created_by = a.CreatedBy,
                        source_label = a.SourceLabel,
                        target_label = a.TargetLabel,
                        audience_snapshot_id = a.AudienceSnapshotId,
                        created_at = a.CreatedAt,
                        started_at = a.StartedAt,
                        ended_at = a.EndedAt,
                        ended_by = a.EndedBy,
                        max_duration_s = a.MaxDurationS,
                        offer_expires_at = a.OfferExpiresAt,
                        chime = a.Chime,
                        targeted = v.Targeted,
                        final_counts = v.FinalCounts,
                        audible_completed = v.AudibleCompleted,
                        socket_delivered = v.SocketDelivered,
                        frames_sent = a.FramesSent,
                        delivery = v.Devices.Select(d => new
                        {
                            device_id = d.DeviceId,
                            label = d.Label,
                            state = d.State,
                            error_code = d.ErrorCode,
                            source = d.Source,
                            at = d.At,
                            states = d.AllStates,
                        }),
                        recording = v.RecordingView(),
                        transcript_status = transcript?.Status ?? "none",
                    };
                }),
                honesty_note = "socket_delivered counts frames written to the device socket; audible_completed counts only devices that reported a completed receipt (PROTOCOL.md §8, plan §12)",
            });
        });

        app.MapPost("/api/history/{announcementId}/correct", async (HttpContext ctx, string announcementId) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            var body = await Http.ReadBody<CorrectRequest>(ctx);
            var res = announcements.CorrectTranscript(user, announcementId, body?.Text ?? "");
            return res.Status == 200 ? Http.Json(new { ok = true }) : Err.Detail(res.Status, res.Detail!);
        });

        app.MapGet("/api/admin/transcription", (HttpContext ctx) =>
        {
            var (user, err) = Office(ctx, read: true);
            if (user is null) return err!;
            return Http.Json(announcements.TranscriptQueueStatus());
        });

        app.MapPost("/api/admin/retention/run", (HttpContext ctx) =>
        {
            var (user, err) = Office(ctx);
            if (user is null) return err!;
            if (user.Role != "admin") return Err.Detail(403, "admin role required");
            var r = announcements.RunRetention(user.Username);
            return Http.Json(new
            {
                audio_deleted = r.AudioDeleted,
                transcripts_purged = r.TranscriptsPurged,
                audio_retention_days = r.AudioRetentionDays,
                transcript_retention_days = r.TranscriptRetentionDays,
            });
        });

        static object leaseJson(TalkLeaseService leases, TalkLease lease) => new
        {
            lease_id = lease.LeaseId,
            holder = lease.Username,
            created_at = lease.CreatedAt,
            expires_at = lease.ExpiresAt,
            max_duration_s = lease.MaxDurationS,
            renew_interval_s = lease.RenewIntervalS,
            renew_deadline = lease.RenewDeadline,
            status = lease.Status,
            announcement_id = lease.AnnouncementId,
        };

        static (string? AnnId, string? LeaseId, IResult? Error) ResolveLiveTarget(
            AnnouncementsService announcements, TalkLeaseService leases, CurrentUser user, string? announcementId)
        {
            if (announcementId is not null)
            {
                var ann = announcements.Read(announcementId);
                if (ann is null) return (null, null, Err.Detail(404, "no such announcement"));
                if (ann.Status != "active") return (null, null, Err.Detail(409, "announcement already ended"));
                if (ann.CreatedBy != user.Username && user.Role != "admin")
                    return (null, null, Err.Detail(403, $"announcement was started by {ann.CreatedBy}"));
                return (ann.AnnouncementId, ann.LeaseId, null);
            }
            var lease = leases.ActiveLease();
            if (lease is null) return (null, null, Err.Detail(404, "no active lease; pass announcement_id"));
            if (lease.Username != user.Username) return (null, null, Err.Detail(403, $"lease held by {lease.Username}"));
            if (lease.AnnouncementId is null) return (null, null, Err.Detail(409, "this lease has no open live announcement"));
            return (lease.AnnouncementId, lease.LeaseId, null);
        }
    }
}

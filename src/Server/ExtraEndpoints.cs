using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

internal sealed record RingRequest(TargetScope? Target, string? SoundId, string? BellName, int? DurationS);
internal sealed record DeviceSettingsRequest(string? OutputDevice, bool? UseDefaultOutput, int? AnnouncementVolume, int? VolumeCap, string? Room);
internal sealed record DefaultsRequest(int? AnnouncementVolume, int? VolumeCap);
internal sealed record RolloutRequest(string? Version, TargetScope? Target, int? BatchSize, int? BatchIntervalS, bool? AllowDowngrade);
internal sealed record ServerUpdateConfigRequest(string? Repo, bool? AutoUpdate, string? Window);
internal sealed record ServerUpdateApplyRequest(string? Version);
internal sealed record EnrollFileRequest(string? Label, int? MaxUses, int? ExpiresHours, List<long>? GroupScope);

/// v1.1 endpoints: bell sounds, ring now, device settings, updates, health, backup/restore.
public static class ExtraEndpoints
{
    public const long SoundMaxBytes = 15 * 1024 * 1024;
    public const int SoundMaxSeconds = 60;

    public static void Map(WebApplication app)
    {
        var db = app.Services.GetRequiredService<Db>();
        var cfg = app.Services.GetRequiredService<ServerConfig>();
        var announcements = app.Services.GetRequiredService<AnnouncementsService>();
        var settings = app.Services.GetRequiredService<DeviceSettingsService>();
        var clientUpdates = app.Services.GetRequiredService<ClientUpdateService>();
        var serverUpdates = app.Services.GetRequiredService<ServerUpdateService>();
        var publish = app.Services.GetRequiredService<PublishService>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SchoolBell.Admin");

        static (CurrentUser? User, IResult? Error) Need(HttpContext ctx, params string[] roles)
        {
            if (Guard.User(ctx) is not CurrentUser u) return (null, Err.Detail(401, "authentication required"));
            if (roles.Length > 0 && !roles.Contains(u.Role)) return (null, Err.Detail(403, $"{string.Join(" or ", roles)} role required"));
            return (u, null);
        }

        app.MapGet("/api/health", () => Results.Json(new { status = "ok", version = Util.ServerVersion }, Util.ApiJson));

        // ---------------------------------------------------------------- sounds

        app.MapGet("/api/sounds", (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx);
            if (u is null) return e!;
            var published = PublishedSoundHashes(db);
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.id, COALESCE(s.name, s.id), s.sha256, s.bytes, s.duration_ms, s.builtin, s.updated_at, s.updated_by,
                       (SELECT COUNT(*) FROM template_periods p WHERE p.sound_id = s.id)
                FROM sound_assets s ORDER BY s.builtin DESC, s.id
                """;
            var list = new List<object>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var sha = r.GetString(2);
                list.Add(new
                {
                    id, name = r.GetString(1), sha256 = sha, bytes = r.GetInt64(3),
                    duration_ms = r.IsDBNull(4) ? (long?)null : r.GetInt64(4),
                    builtin = !r.IsDBNull(5) && r.GetInt32(5) == 1 || SoundService.BuiltIn.Any(b => b.Id == id),
                    updated_at = r.IsDBNull(6) ? (long?)null : r.GetInt64(6),
                    updated_by = r.IsDBNull(7) ? null : r.GetString(7),
                    used_by_periods = r.GetInt32(8),
                    url = $"api/sounds/{id}?sha={sha}",
                    // the cached schedule on endpoints still has the old audio until the next publish
                    needs_publish = published.TryGetValue(id, out var ps) && ps != sha,
                });
            }
            return Http.Json(list);
        });

        // upload a new sound, or replace an existing one (including the built-in std-bell)
        async Task<IResult> SaveSound(HttpContext ctx, string? fixedId)
        {
            var (u, e) = Need(ctx, "admin", "operator");
            if (u is null) return e!;
            if (!ctx.Request.HasFormContentType) return Err.Detail(400, "multipart/form-data with a 'file' field is required");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            var name = form["name"].ToString().Trim();
            string id = fixedId ?? form["id"].ToString().Trim().ToLowerInvariant();
            if (fixedId is null)
            {
                if (id == "") id = "snd-" + new string((name == "" ? "sound" : name).ToLowerInvariant()
                    .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
                if (id.Length > 40) id = id[..40];
                if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return Err.Detail(400, "sound id may contain only letters, digits, '-' and '_'");
            }
            using (var conn = db.Open())
            {
                bool exists = conn.Exists("SELECT COUNT(*) FROM sound_assets WHERE id = $i", ("$i", id));
                if (fixedId is not null && !exists) return Err.Detail(404, "no such sound");
                if (fixedId is null && exists) return Err.Detail(409, $"a sound with id '{id}' already exists (use replace)");
                if (file is null && fixedId is not null && name != "")
                {
                    conn.Run("UPDATE sound_assets SET name = $n, updated_at = $t, updated_by = $u WHERE id = $i",
                        ("$n", name), ("$t", Now()), ("$u", u.Username), ("$i", id));
                    return Http.Json(new { ok = true, id });
                }
            }
            if (file is null || file.Length == 0) return Err.Detail(400, "a WAV 'file' field is required");
            if (file.Length > SoundMaxBytes) return Err.Detail(400, $"sound is {file.Length} bytes; the limit is {SoundMaxBytes / 1024 / 1024} MB");
            byte[] bytes;
            await using (var s = file.OpenReadStream()) { using var ms = new MemoryStream(); await s.CopyToAsync(ms); bytes = ms.ToArray(); }
            var (ok, detail, durationMs) = ValidateBellWav(bytes);
            if (!ok) return Err.Detail(400, detail!);

            Directory.CreateDirectory(cfg.SoundsDir);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var dest = Path.Combine(cfg.SoundsDir, $"{id}.wav");
            var tmp = dest + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, dest, overwrite: true);
            using (var conn = db.Open())
            {
                conn.Run("""
                    INSERT INTO sound_assets(id, sha256, file_path, bytes, name, duration_ms, builtin, updated_at, updated_by)
                    VALUES ($i, $s, $p, $b, $n, $d, 0, $t, $u)
                    ON CONFLICT(id) DO UPDATE SET sha256 = $s, file_path = $p, bytes = $b,
                        name = COALESCE(NULLIF($n, ''), name), duration_ms = $d, updated_at = $t, updated_by = $u
                    """,
                    ("$i", id), ("$s", sha), ("$p", dest), ("$b", (long)bytes.Length), ("$n", name), ("$d", durationMs),
                    ("$t", Now()), ("$u", u.Username));
                Audit.Add(conn, null, u.Username, fixedId is null ? "sound.upload" : "sound.replace",
                    $"sound={id} bytes={bytes.Length} sha256={sha} duration_ms={durationMs}");
            }
            log.LogInformation("sound {Id} {Action} by {User} ({Bytes} bytes)", id, fixedId is null ? "uploaded" : "replaced", u.Username, bytes.Length);
            return Http.Json(new { ok = true, id, sha256 = sha, duration_ms = durationMs, publish_needed = fixedId is not null }, fixedId is null ? 201 : 200);
        }

        app.MapPost("/api/sounds", (HttpContext ctx) => SaveSound(ctx, null));
        app.MapPut("/api/sounds/{id}", (HttpContext ctx, string id) => SaveSound(ctx, id));

        app.MapDelete("/api/sounds/{id}", (HttpContext ctx, string id) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            if (SoundService.BuiltIn.Any(b => b.Id == id)) return Err.Detail(409, "built-in sounds can be replaced but not deleted");
            using var conn = db.Open();
            var uses = conn.ScalarInt("SELECT COUNT(*) FROM template_periods WHERE sound_id = $i", ("$i", id));
            if (uses > 0) return Err.Detail(409, $"sound is used by {uses} schedule period(s); change those first");
            var path = conn.Scalar("SELECT file_path FROM sound_assets WHERE id = $i", ("$i", id)) as string;
            if (path is null) return Err.Detail(404, "no such sound");
            conn.Run("DELETE FROM sound_assets WHERE id = $i", ("$i", id));
            try { File.Delete(path); } catch (IOException) { }
            Audit.Add(conn, null, u.Username, "sound.delete", $"sound={id}");
            return Http.Json(new { ok = true });
        });

        // ---------------------------------------------------------------- ring now

        app.MapPost("/api/bells/ring", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin", "operator");
            if (u is null) return e!;
            var body = await Http.ReadBody<RingRequest>(ctx);
            if (body?.Target is null) return Err.Detail(400, "target is required (example: {\"all\":true})");
            var res = announcements.RingNow(u, body.Target, body.SoundId, body.BellName, body.DurationS);
            if (res.RingId is null) return Err.Detail(res.Status, res.Detail!);
            return Http.Json(new { ring_id = res.RingId, device_count = res.DeviceCount, target_label = res.TargetLabel,
                expires_in_s = Protocol.ManualRingTtlSeconds }, 201);
        });

        app.MapGet("/api/bells/rings", (HttpContext ctx, int limit = 20) =>
        {
            var (u, e) = Need(ctx);
            if (u is null) return e!;
            return Http.Json(announcements.RecentRings(limit));
        });

        // ---------------------------------------------------------------- device settings

        app.MapGet("/api/admin/devices/{deviceId}/settings", (HttpContext ctx, string deviceId) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            return Http.Json(settings.For(deviceId));
        });

        app.MapPut("/api/admin/devices/{deviceId}/settings", async (HttpContext ctx, string deviceId) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var b = await Http.ReadBody<DeviceSettingsRequest>(ctx);
            if (b is null) return Err.Detail(400, "request body must be a JSON object");
            var (status, detail) = settings.Update(u.Username, deviceId, b.OutputDevice, b.UseDefaultOutput == true,
                b.AnnouncementVolume, b.VolumeCap, b.Room);
            return status == 200 ? Http.Json(settings.For(deviceId)) : Err.Detail(status, detail!);
        });

        app.MapGet("/api/admin/device-defaults", (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            return Http.Json(new { announcement_volume = settings.DefaultAnnouncementVolume, volume_cap = settings.DefaultVolumeCap });
        });

        app.MapPut("/api/admin/device-defaults", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var b = await Http.ReadBody<DefaultsRequest>(ctx);
            settings.SetDefaults(u.Username, b?.AnnouncementVolume, b?.VolumeCap);
            return Http.Json(new { announcement_volume = settings.DefaultAnnouncementVolume, volume_cap = settings.DefaultVolumeCap });
        });

        // ---------------------------------------------------------------- client updates

        app.MapGet("/api/admin/updates/client", (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            return Http.Json(clientUpdates.Overview());
        });

        app.MapGet("/api/admin/updates/client/rollouts/{id}", (HttpContext ctx, string id) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            return Http.Json(clientUpdates.RolloutDetail(id));
        });

        app.MapPost("/api/admin/updates/client/upload", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            if (!ctx.Request.HasFormContentType) return Err.Detail(400, "multipart with 'setup', 'manifest' and 'signature' files is required");
            var form = await ctx.Request.ReadFormAsync();
            var setup = form.Files.GetFile("setup");
            var manifest = form.Files.GetFile("manifest");
            var sig = form.Files.GetFile("signature");
            if (setup is null || manifest is null || sig is null) return Err.Detail(400, "'setup', 'manifest' and 'signature' files are required");
            if (setup.Length > 600L * 1024 * 1024) return Err.Detail(400, "installer is too large");
            byte[] setupBytes;
            await using (var s = setup.OpenReadStream()) { using var ms = new MemoryStream(); await s.CopyToAsync(ms); setupBytes = ms.ToArray(); }
            string manifestText, sigText;
            using (var sr = new StreamReader(manifest.OpenReadStream())) manifestText = await sr.ReadToEndAsync();
            using (var sr = new StreamReader(sig.OpenReadStream())) sigText = await sr.ReadToEndAsync();
            var (status, detail, version) = clientUpdates.Import(manifestText, sigText, setupBytes);
            if (status != 200) return Err.Detail(status, detail!);
            Audit.Add(db, u.Username, "client_update.upload", $"version={version}");
            return Http.Json(new { ok = true, version });
        });

        app.MapPost("/api/admin/updates/client/rollout", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var b = await Http.ReadBody<RolloutRequest>(ctx);
            if (b?.Version is null || b.Target is null) return Err.Detail(400, "version and target are required");
            var (status, detail, id, n) = clientUpdates.StartRollout(u, b.Version, b.Target, b.BatchSize ?? 10, b.BatchIntervalS ?? 300, b.AllowDowngrade == true);
            return id is null ? Err.Detail(status, detail!) : Http.Json(new { rollout_id = id, devices = n }, 201);
        });

        app.MapDelete("/api/admin/updates/client/rollouts/{id}", (HttpContext ctx, string id) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var (status, detail) = clientUpdates.CancelRollout(u, id);
            return status == 200 ? Http.Json(new { ok = true }) : Err.Detail(status, detail!);
        });

        // device download: per-device HMAC link from update.offer (no office session involved)
        app.MapGet("/api/updates/client/{version}/SchoolBellSetup.exe", (string version, string? device, long? exp, string? sig) =>
        {
            var path = clientUpdates.AuthorizeDownload(version, device ?? "", exp ?? 0, sig ?? "");
            return path is null ? Err.Detail(403, "invalid or expired update link")
                : Results.File(path, "application/vnd.microsoft.portable-executable", "SchoolBellSetup.exe");
        });

        // ---------------------------------------------------------------- server self-update

        app.MapGet("/api/admin/updates/server", (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            return Http.Json(serverUpdates.Status());
        });

        app.MapPost("/api/admin/updates/server/check", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            return Http.Json(await serverUpdates.CheckAsync(ctx.RequestAborted));
        });

        app.MapPut("/api/admin/updates/server/config", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var b = await Http.ReadBody<ServerUpdateConfigRequest>(ctx);
            try { serverUpdates.Configure(u.Username, b?.Repo, b?.AutoUpdate, b?.Window); }
            catch (ArgumentException ex) { return Err.Detail(400, ex.Message); }
            return Http.Json(serverUpdates.Status());
        });

        app.MapPost("/api/admin/updates/server/apply", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            if (announcements.ActiveAnnouncementCount() > 0) return Err.Detail(409, "an announcement is in progress; try again when it ends");
            var b = await Http.ReadBody<ServerUpdateApplyRequest>(ctx);
            var (status, detail) = serverUpdates.RequestApply(u.Username, b?.Version);
            return status == 202 ? Http.Json(new { ok = true, detail = "update requested; the server restarts when it is installed" }, 202)
                : Err.Detail(status, detail!);
        });

        // ---------------------------------------------------------------- enrollment file

        app.MapPost("/api/admin/enrollment-file", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var b = await Http.ReadBody<EnrollFileRequest>(ctx) ?? new EnrollFileRequest(null, null, null, null);
            var enrollment = ctx.RequestServices.GetRequiredService<EnrollmentService>();
            int hours = Math.Clamp(b.ExpiresHours ?? 24, 1, 24 * 14);
            var res = enrollment.CreateToken(u.Username, b.Label ?? "enrollment file", hours, Math.Clamp(b.MaxUses ?? 1, 1, 100_000),
                b.GroupScope ?? []);
            if (res.Token is null || res.Info is null) return Err.Detail(res.Status, res.Detail ?? "token not created");
            var (token, row) = (res.Token, res.Info);
            var server = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            if (ctx.Request.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } fh)
                server = $"{(ctx.Request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } fp ? fp : "https")}://{fh}";
            var json = JsonSerializer.Serialize(new
            {
                v = 1, server, token, expires_at = row.ExpiresAt, label = row.Label, max_uses = row.MaxUses,
                note = "Deployment material: keep private, delete after use.",
            }, new JsonSerializerOptions { WriteIndented = true });
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "schoolbell-enroll.json");
        });

        // ---------------------------------------------------------------- backup / restore

        app.MapPost("/api/admin/backup", (HttpContext ctx, bool? include_audio) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var path = Backup.Create(db, cfg, include_audio == true);
            Audit.Add(db, u.Username, "backup.create", $"file={Path.GetFileName(path)} bytes={new FileInfo(path).Length}");
            return Http.Json(new { ok = true, file = Path.GetFileName(path), bytes = new FileInfo(path).Length }, 201);
        });

        app.MapGet("/api/admin/backups", (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            return Http.Json(Backup.List(cfg));
        });

        app.MapGet("/api/admin/backups/{file}", (HttpContext ctx, string file) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            var path = Backup.Resolve(cfg, file);
            return path is null ? Err.Detail(404, "no such backup") : Results.File(path, "application/gzip", file);
        });

        app.MapPost("/api/admin/restore", async (HttpContext ctx) =>
        {
            var (u, e) = Need(ctx, "admin");
            if (u is null) return e!;
            if (!ctx.Request.HasFormContentType) return Err.Detail(400, "multipart with a 'file' (backup .tar.gz) is required");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            string? existing = form["backup"].ToString() is { Length: > 0 } name ? Backup.Resolve(cfg, name) : null;
            if (file is null && existing is null) return Err.Detail(400, "upload a backup file or name an existing backup");
            string src = existing ?? Path.Combine(Backup.Dir(cfg), "upload-" + Guid.NewGuid().ToString("N") + ".tar.gz");
            if (existing is null) await using (var fs = File.Create(src)) await file!.CopyToAsync(fs);
            var (ok, detail) = Backup.StageRestore(cfg, src);
            if (!ok) return Err.Detail(400, detail!);
            Audit.Add(db, u.Username, "backup.restore_staged", $"source={Path.GetFileName(src)}");
            log.LogWarning("restore staged by {User}; restarting to apply it", u.Username);
            _ = Task.Run(async () => { await Task.Delay(1500); Environment.Exit(3); }); // systemd Restart=always
            return Http.Json(new { ok = true, detail = "restore staged; SchoolBell restarts now and applies it (the previous database is kept)" }, 202);
        });
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Dictionary<string, string> PublishedSoundHashes(Db db)
    {
        using var conn = db.Open();
        var json = conn.Scalar("SELECT manifest_json FROM published_revisions ORDER BY revision DESC LIMIT 1") as string;
        var map = new Dictionary<string, string>();
        if (json is null) return map;
        try
        {
            var m = JsonSerializer.Deserialize<ScheduleManifestPayload>(json, ControlEnvelope.Json);
            foreach (var s in m?.Sounds ?? []) map[s.Id] = s.Sha256;
        }
        catch (JsonException) { }
        return map;
    }

    /// Bell sounds: RIFF/WAVE PCM 16/24-bit or 32-bit float, mono or stereo, 8-48 kHz, ≤60 s.
    public static (bool Ok, string? Detail, long DurationMs) ValidateBellWav(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            return (false, "the file is not a WAV (RIFF/WAVE) file", 0);
        int pos = 12, fmt = 0, ch = 0, rate = 0, bits = 0; long data = -1;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            if (size < 0) break;
            if (id == "fmt " && pos + 24 <= b.Length)
            {
                fmt = BitConverter.ToUInt16(b, pos + 8); ch = BitConverter.ToUInt16(b, pos + 10);
                rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToUInt16(b, pos + 22);
                if (fmt == 0xFFFE && pos + 34 <= b.Length) fmt = BitConverter.ToUInt16(b, pos + 32);
            }
            else if (id == "data") { data = Math.Min(size, b.Length - pos - 8); break; }
            pos += 8 + size + (size & 1);
        }
        if (data <= 0) return (false, "the WAV has no audio data", 0);
        if (!(fmt == 1 && bits is 16 or 24) && !(fmt == 3 && bits == 32))
            return (false, $"unsupported WAV encoding (format {fmt}, {bits}-bit); use 16-bit or 24-bit PCM", 0);
        if (ch is < 1 or > 2) return (false, "the WAV must be mono or stereo", 0);
        if (rate is < 8000 or > 48000) return (false, $"sample rate {rate} Hz is outside 8-48 kHz", 0);
        long ms = data * 1000 / Math.Max(1, (long)rate * ch * (bits / 8));
        if (ms > SoundMaxSeconds * 1000L) return (false, $"the sound is {ms / 1000} s long; the limit is {SoundMaxSeconds} s", 0);
        if (ms < 100) return (false, "the sound is shorter than 0.1 s", 0);
        return (true, null, ms);
    }
}

/// Consistent backups (SQLite VACUUM INTO + sounds + keys [+ recordings]) and staged restore.
public static class Backup
{
    public static string Dir(ServerConfig cfg)
    {
        var d = Path.Combine(cfg.DataDir, "backups");
        Directory.CreateDirectory(d);
        return d;
    }

    public static string Create(Db db, ServerConfig cfg, bool includeAudio)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var work = Path.Combine(Dir(cfg), $".work-{stamp}");
        Directory.CreateDirectory(work);
        try
        {
            var snap = Path.Combine(work, "schoolbell.db");
            using (var conn = db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "VACUUM INTO $p";
                cmd.Parameters.AddWithValue("$p", snap);
                cmd.ExecuteNonQuery();
            }
            var outPath = Path.Combine(Dir(cfg), $"schoolbell-backup-{stamp}.tar.gz");
            using (var fs = File.Create(outPath))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
            {
                tar.WriteEntry(snap, "schoolbell.db");
                foreach (var sub in includeAudio ? new[] { "sounds", "keys", "audio" } : new[] { "sounds", "keys" })
                {
                    var dir = Path.Combine(cfg.DataDir, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        tar.WriteEntry(f, Path.Combine(sub, Path.GetRelativePath(dir, f)).Replace('\\', '/'));
                }
                var meta = Path.Combine(work, "backup.json");
                File.WriteAllText(meta, JsonSerializer.Serialize(new { version = Util.ServerVersion, created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), include_audio = includeAudio }));
                tar.WriteEntry(meta, "backup.json");
            }
            return outPath;
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    public static List<object> List(ServerConfig cfg) =>
        new DirectoryInfo(Dir(cfg)).GetFiles("*.tar.gz").OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (object)new { file = f.Name, bytes = f.Length, created_at = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds() })
            .ToList();

    public static string? Resolve(ServerConfig cfg, string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file.Contains("..") || !file.EndsWith(".tar.gz")) return null;
        var p = Path.Combine(Dir(cfg), file);
        return File.Exists(p) ? p : null;
    }

    /// Unpacks into restore-pending/ after validating the database; applied at next start.
    public static (bool Ok, string? Detail) StageRestore(ServerConfig cfg, string archive)
    {
        var stage = Path.Combine(cfg.DataDir, "restore-pending");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        try
        {
            using (var fs = File.OpenRead(archive))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var tar = new TarReader(gz))
            {
                TarEntry? e;
                while ((e = tar.GetNextEntry()) is not null)
                {
                    if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                    var rel = e.Name.Replace('\\', '/');
                    if (rel.StartsWith('/') || rel.Contains("..")) return (false, "the archive contains an unsafe path");
                    var dest = Path.GetFullPath(Path.Combine(stage, rel));
                    if (!dest.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar)) return (false, "the archive contains an unsafe path");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                }
            }
            var dbPath = Path.Combine(stage, "schoolbell.db");
            if (!File.Exists(dbPath)) return (false, "the archive has no schoolbell.db");
            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                if (cmd.ExecuteScalar() as string != "ok") return (false, "the backup database failed its integrity check");
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('devices','settings','users')";
                if (Convert.ToInt32(cmd.ExecuteScalar()) < 3) return (false, "the backup is not a SchoolBell database");
            }
            File.WriteAllText(Path.Combine(stage, "READY"), DateTimeOffset.UtcNow.ToString("o"));
            return (true, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SqliteException)
        {
            return (false, $"the backup could not be read: {ex.Message}");
        }
    }

    /// Called before the database opens: swap in a staged restore, keeping the old files.
    public static void ApplyPendingRestore(ServerConfig cfg, ILogger log)
    {
        var stage = Path.Combine(cfg.DataDir, "restore-pending");
        if (!File.Exists(Path.Combine(stage, "READY"))) return;
        var keep = Path.Combine(cfg.DataDir, "backups", $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(keep);
        foreach (var f in new[] { "schoolbell.db", "schoolbell.db-wal", "schoolbell.db-shm" })
        {
            var p = Path.Combine(cfg.DataDir, f);
            if (File.Exists(p)) File.Move(p, Path.Combine(keep, f));
        }
        foreach (var sub in new[] { "sounds", "keys", "audio" })
        {
            var src = Path.Combine(stage, sub);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(cfg.DataDir, sub);
            if (Directory.Exists(dst)) Directory.Move(dst, Path.Combine(keep, sub));
            Directory.Move(src, dst);
        }
        File.Move(Path.Combine(stage, "schoolbell.db"), cfg.DbPath);
        Directory.Delete(stage, true);
        log.LogWarning("restored database from backup; previous files kept in {Keep}", keep);
    }
}

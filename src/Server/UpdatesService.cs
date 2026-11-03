using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// Publisher key + release file helpers shared by client and server updates.
public sealed class ReleaseTrust
{
    private readonly ServerConfig _cfg;
    public ReleaseTrust(ServerConfig cfg) => _cfg = cfg;

    /// /etc/schoolbell/release-signing.pub.pem in production (installed from the bundle the
    /// administrator verified); the repo copy in development.
    public string? PublicKeyPem
    {
        get
        {
            foreach (var p in new[] { _cfg.ReleaseKeyPath,
                         Path.Combine(AppContext.BaseDirectory, "release-signing.pub.pem"),
                         Util.FindUpwards(AppContext.BaseDirectory, "build", "release-signing.pub.pem") })
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return File.ReadAllText(p);
            return null;
        }
    }

    public ReleaseManifest? Verify(string manifestText, string signature) =>
        PublicKeyPem is { } pem ? ReleaseSignature.ParseVerified(manifestText, signature, pem) : null;
}

public sealed record ClientRelease(string Version, string Commit, long CreatedAt, long Bytes, string Sha256, string Dir);

/// Server-pushed Windows client updates (PROTOCOL §13 update.offer). Releases come from the
/// server bundle (client-release/ inside the server install) or an /admin upload, and are
/// accepted only when the publisher signature verifies. Rollouts are staged in batches.
public sealed class ClientUpdateService
{
    public const string Artifact = "windows/SchoolBellSetup.exe";
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly ReleaseTrust _trust;
    private readonly OutboxService _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger<ClientUpdateService> _log;

    public ClientUpdateService(Db db, ServerConfig cfg, ReleaseTrust trust, OutboxService outbox, TimeProvider time,
        ILogger<ClientUpdateService> log)
    {
        _db = db; _cfg = cfg; _trust = trust; _outbox = outbox; _time = time; _log = log;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();
    public string ReleasesDir => Path.Combine(_cfg.DataDir, "releases", "client");

    /// Import a signed client release (manifest + signature + installer). Returns the version.
    public (int Status, string? Detail, string? Version) Import(string manifestText, string signature, byte[] setup)
    {
        var m = _trust.Verify(manifestText, signature);
        if (m is null) return (400, "the release signature does not verify against the pinned publisher key", null);
        if (!m.Files.TryGetValue(Artifact, out var f)) return (400, $"the signed manifest does not list {Artifact}", null);
        var sha = Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant();
        if (sha != f.Sha256.ToLowerInvariant() || setup.LongLength != f.Bytes)
            return (400, "the installer does not match the signed manifest (sha256/size)", null);
        var dir = Path.Combine(ReleasesDir, Safe(m.Version));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "SchoolBellSetup.exe.tmp"), setup);
        File.Move(Path.Combine(dir, "SchoolBellSetup.exe.tmp"), Path.Combine(dir, "SchoolBellSetup.exe"), overwrite: true);
        File.WriteAllText(Path.Combine(dir, "release-manifest.json"), manifestText);
        File.WriteAllText(Path.Combine(dir, "release-manifest.json.sig"), signature.Trim());
        _log.LogInformation("client release {Version} imported (sha256 {Sha})", m.Version, sha[..12]);
        return (200, null, m.Version);
    }

    /// The server release bundle carries the matching Windows client (client-release/).
    public void ImportFromBundle()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "client-release");
        var manifest = Path.Combine(dir, "release-manifest.json");
        var sig = Path.Combine(dir, "release-manifest.json.sig");
        var setup = Path.Combine(dir, "windows", "SchoolBellSetup.exe");
        if (!File.Exists(manifest) || !File.Exists(sig) || !File.Exists(setup)) return;
        try
        {
            var res = Import(File.ReadAllText(manifest), File.ReadAllText(sig), File.ReadAllBytes(setup));
            if (res.Status != 200) _log.LogWarning("bundled client release not imported: {Detail}", res.Detail);
        }
        catch (Exception e) { _log.LogWarning("bundled client release import failed: {Error}", e.Message); }
    }

    public List<ClientRelease> Releases()
    {
        var list = new List<ClientRelease>();
        if (!Directory.Exists(ReleasesDir)) return list;
        foreach (var dir in Directory.GetDirectories(ReleasesDir))
        {
            var mp = Path.Combine(dir, "release-manifest.json");
            var sp = Path.Combine(dir, "release-manifest.json.sig");
            if (!File.Exists(mp) || !File.Exists(sp) || !File.Exists(Path.Combine(dir, "SchoolBellSetup.exe"))) continue;
            var m = _trust.Verify(File.ReadAllText(mp), File.ReadAllText(sp));
            if (m is null || !m.Files.TryGetValue(Artifact, out var f)) continue;
            list.Add(new ClientRelease(m.Version, m.Commit, m.CreatedAt, f.Bytes, f.Sha256, dir));
        }
        return list.OrderByDescending(r => r.Version, Comparer<string>.Create(ReleaseSignature.CompareVersions)).ToList();
    }

    public object Overview()
    {
        using var conn = _db.Open();
        var versions = new List<object>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(client_version, 'unknown'), COUNT(*) FROM devices WHERE revoked = 0 GROUP BY 1 ORDER BY 2 DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) versions.Add(new { version = r.GetString(0), devices = r.GetInt32(1) });
        }
        var rollouts = new List<object>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.rollout_id, r.version, r.created_by, r.created_at, r.status, r.batch_size, r.batch_interval_s,
                       (SELECT COUNT(*) FROM client_rollout_devices d WHERE d.rollout_id = r.rollout_id),
                       (SELECT COUNT(*) FROM client_rollout_devices d WHERE d.rollout_id = r.rollout_id AND d.state = 'installed'),
                       (SELECT COUNT(*) FROM client_rollout_devices d WHERE d.rollout_id = r.rollout_id AND d.state IN ('failed','rolled_back'))
                FROM client_rollouts r ORDER BY r.created_at DESC LIMIT 20
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rollouts.Add(new
                {
                    rollout_id = r.GetString(0), version = r.GetString(1), created_by = r.GetString(2), created_at = r.GetInt64(3),
                    status = r.GetString(4), batch_size = r.GetInt32(5), batch_interval_s = r.GetInt32(6),
                    devices = r.GetInt32(7), installed = r.GetInt32(8), failed = r.GetInt32(9),
                });
        }
        return new
        {
            publisher_key = _trust.PublicKeyPem is not null,
            releases = Releases().Select(r => new { r.Version, r.Commit, r.CreatedAt, r.Bytes, r.Sha256 }),
            device_versions = versions,
            rollouts,
        };
    }

    public object RolloutDetail(string rolloutId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.device_id, COALESCE(v.computer_name, v.name), v.client_version, d.batch, d.not_before, d.offered_at, d.state, d.error, d.updated_at
            FROM client_rollout_devices d JOIN devices v ON v.device_id = d.device_id
            WHERE d.rollout_id = $r ORDER BY d.batch, 2
            """;
        cmd.Parameters.AddWithValue("$r", rolloutId);
        var list = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new
            {
                device_id = r.GetString(0), computer = r.GetString(1), client_version = r.IsDBNull(2) ? null : r.GetString(2),
                batch = r.GetInt32(3), not_before = r.GetInt64(4), offered_at = r.IsDBNull(5) ? (long?)null : r.GetInt64(5),
                state = r.GetString(6), error = r.IsDBNull(7) ? null : r.GetString(7), updated_at = r.GetInt64(8),
            });
        return list;
    }

    public (int Status, string? Detail, string? RolloutId, int Devices) StartRollout(CurrentUser user, string version,
        TargetScope target, int batchSize, int batchIntervalS, bool allowDowngrade)
    {
        if (user.Role != "admin") return (403, "only administrators can roll out client updates", null, 0);
        var release = Releases().FirstOrDefault(r => r.Version == version);
        if (release is null) return (404, $"no verified client release {version} on this server", null, 0);
        batchSize = Math.Clamp(batchSize <= 0 ? 10 : batchSize, 1, 100_000);
        batchIntervalS = Math.Clamp(batchIntervalS < 0 ? 300 : batchIntervalS, 0, 86_400);
        var id = Guid.NewGuid().ToString("N")[..12];
        long now = NowMs;
        using var conn = _db.Open();
        var devices = ResolveDevices(conn, target);
        if (devices.Count == 0) return (409, "no enrolled, non-revoked devices match this target", null, 0);
        using var tx = conn.BeginTransaction();
        conn.Run(tx, """
            INSERT INTO client_rollouts(rollout_id, version, created_by, created_at, target_json, batch_size, batch_interval_s, allow_downgrade)
            VALUES ($i, $v, $u, $t, $tj, $b, $bi, $ad)
            """, ("$i", id), ("$v", version), ("$u", user.Username), ("$t", now),
            ("$tj", TalkLeaseService.WriteTarget(target)), ("$b", batchSize), ("$bi", batchIntervalS), ("$ad", allowDowngrade ? 1 : 0));
        for (int i = 0; i < devices.Count; i++)
        {
            int batch = i / batchSize;
            conn.Run(tx, """
                INSERT INTO client_rollout_devices(rollout_id, device_id, batch, not_before, state, updated_at)
                VALUES ($i, $d, $b, $nb, 'pending', $t)
                """, ("$i", id), ("$d", devices[i]), ("$b", batch), ("$nb", now + batch * batchIntervalS * 1000L), ("$t", now));
        }
        tx.Commit();
        Audit.Add(conn, null, user.Username, "client_update.rollout",
            $"rollout={id} version={version} devices={devices.Count} batch_size={batchSize} interval_s={batchIntervalS}");
        conn.Dispose();
        Tick();
        return (201, null, id, devices.Count);
    }

    public (int Status, string? Detail) CancelRollout(CurrentUser user, string rolloutId)
    {
        if (user.Role != "admin") return (403, "only administrators can cancel rollouts");
        using var conn = _db.Open();
        int n = conn.Run("UPDATE client_rollouts SET status = 'cancelled' WHERE rollout_id = $r AND status = 'active'", ("$r", rolloutId));
        conn.Run("UPDATE client_rollout_devices SET state = 'cancelled', updated_at = $t WHERE rollout_id = $r AND state = 'pending'",
            ("$t", NowMs), ("$r", rolloutId));
        Audit.Add(conn, null, user.Username, "client_update.cancel", $"rollout={rolloutId}");
        return n == 1 ? (200, null) : (404, "no active rollout with that id");
    }

    private static List<string> ResolveDevices(SqliteConnection conn, TargetScope target)
    {
        string sql; (string, object?)[] ps = [];
        if (target.All == true) sql = "SELECT device_id FROM devices WHERE revoked = 0 ORDER BY device_id";
        else if (target.Groups is { Count: > 0 })
        {
            ps = target.Groups.Select((g, i) => ($"$g{i}", (object?)g)).ToArray();
            sql = $"SELECT DISTINCT d.device_id FROM devices d JOIN group_members gm ON gm.device_id = d.device_id WHERE d.revoked = 0 AND gm.group_id IN ({string.Join(",", ps.Select(p => p.Item1))}) ORDER BY d.device_id";
        }
        else if (target.Devices is { Count: > 0 })
        {
            ps = target.Devices.Select((d, i) => ($"$d{i}", (object?)d)).ToArray();
            sql = $"SELECT device_id FROM devices WHERE revoked = 0 AND device_id IN ({string.Join(",", ps.Select(p => p.Item1))}) ORDER BY device_id";
        }
        else return [];
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// Offer the update to every pending device whose batch time has come (the outbox delivers
    /// to offline devices when they reconnect).
    public int Tick()
    {
        long now = NowMs;
        var due = new List<(string Rollout, string Version, bool Downgrade, string Device, long NotBefore)>();
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.rollout_id, r.version, r.allow_downgrade, d.device_id, d.not_before
                FROM client_rollouts r JOIN client_rollout_devices d ON d.rollout_id = r.rollout_id
                WHERE r.status = 'active' AND d.state = 'pending' AND d.not_before <= $n
                LIMIT 500
                """;
            cmd.Parameters.AddWithValue("$n", now);
            using var r = cmd.ExecuteReader();
            while (r.Read()) due.Add((r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, r.GetString(3), r.GetInt64(4)));
        }
        if (due.Count == 0) { CompleteFinished(); return 0; }
        var releases = Releases().ToDictionary(r => r.Version);
        foreach (var d in due)
        {
            if (!releases.TryGetValue(d.Version, out var rel)) continue;
            var manifest = File.ReadAllText(Path.Combine(rel.Dir, "release-manifest.json"));
            var sig = File.ReadAllText(Path.Combine(rel.Dir, "release-manifest.json.sig")).Trim();
            long exp = now + 24 * 3600_000L;
            var url = $"/api/updates/client/{Uri.EscapeDataString(d.Version)}/SchoolBellSetup.exe?device={Uri.EscapeDataString(d.Device)}&exp={exp}&sig={DownloadSig(d.Device, d.Version, exp)}";
            var env = ControlEnvelope.Create(Protocol.TUpdateOffer, PayloadSerializer.Node(new UpdateOfferPayload(
                d.Version, Artifact, url, rel.Bytes, manifest, sig, d.Downgrade, d.NotBefore)));
            _outbox.Enqueue(d.Device, env);
            using var conn = _db.Open();
            conn.Run("UPDATE client_rollout_devices SET state = 'offered', offered_at = $t, updated_at = $t WHERE rollout_id = $r AND device_id = $d",
                ("$t", now), ("$r", d.Rollout), ("$d", d.Device));
        }
        _log.LogInformation("client update offers sent to {Count} device(s)", due.Count);
        return due.Count;
    }

    private void CompleteFinished()
    {
        using var conn = _db.Open();
        conn.Run("""
            UPDATE client_rollouts SET status = 'completed'
            WHERE status = 'active' AND NOT EXISTS (
              SELECT 1 FROM client_rollout_devices d WHERE d.rollout_id = client_rollouts.rollout_id
                AND d.state IN ('pending','offered','received','downloading','verified','deferred','installing'))
            """);
    }

    private string DownloadSig(string deviceId, string version, long exp)
    {
        using var conn = _db.Open();
        var secret = conn.Scalar("SELECT secret FROM devices WHERE device_id = $d", ("$d", deviceId)) as string ?? "";
        return Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(secret),
            Encoding.UTF8.GetBytes($"update|{version}|{exp}"))).ToLowerInvariant();
    }

    /// Validates a per-device download link (HMAC with that device's secret, 24 h expiry).
    public string? AuthorizeDownload(string version, string deviceId, long exp, string sig)
    {
        if (exp < NowMs || string.IsNullOrEmpty(sig)) return null;
        using (var conn = _db.Open())
            if (!conn.Exists("SELECT COUNT(*) FROM devices WHERE device_id = $d AND revoked = 0", ("$d", deviceId))) return null;
        if (!Util.HexEqualsConstantTime(DownloadSig(deviceId, version, exp), sig)) return null;
        var rel = Releases().FirstOrDefault(r => r.Version == version);
        return rel is null ? null : Path.Combine(rel.Dir, "SchoolBellSetup.exe");
    }

    public void OnDeviceStatus(string deviceId, UpdateStatusPayload s)
    {
        using var conn = _db.Open();
        conn.Run("UPDATE devices SET update_json = $j WHERE device_id = $d",
            ("$j", JsonSerializer.Serialize(s, ControlEnvelope.Json)), ("$d", deviceId));
        conn.Run("""
            UPDATE client_rollout_devices SET state = $s, error = $e, updated_at = $t
            WHERE device_id = $d AND rollout_id IN (SELECT rollout_id FROM client_rollouts WHERE version = $v)
            """, ("$s", s.State), ("$e", s.Error), ("$t", NowMs), ("$d", deviceId), ("$v", s.Version));
    }

    private static string Safe(string v) => new(v.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());
}

/// Server self-update from GitHub Releases. The web app (unprivileged) only checks and records a
/// request; the root-run systemd unit schoolbell-update.service (triggered by a path unit on the
/// request file) re-verifies the publisher signature itself, takes a consistent backup, installs,
/// health-checks and rolls back automatically (install/server/schoolbell-update.sh).
public sealed class ServerUpdateService
{
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly ReleaseTrust _trust;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerUpdateService> _log;
    private JsonObject? _latest;
    private string? _lastError;
    private long _lastCheckAt;

    public ServerUpdateService(Db db, ServerConfig cfg, ReleaseTrust trust, IHttpClientFactory httpFactory,
        TimeProvider time, ILogger<ServerUpdateService> log)
    {
        _db = db; _cfg = cfg; _trust = trust; _httpFactory = httpFactory; _time = time; _log = log;
    }

    public string Repo => _db.GetSetting("update_repo") is { Length: > 0 } r ? r : _cfg.UpdateRepo;
    public bool AutoUpdate => _db.GetSetting("auto_update") == "true";
    public string Window => _db.GetSetting("update_window") is { Length: > 0 } w ? w : "02:00-04:00";
    private string UpdateDir => Path.Combine(_cfg.DataDir, "update");

    public void Configure(string actor, string? repo, bool? autoUpdate, string? window)
    {
        if (repo is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                throw new ArgumentException("repo must look like owner/name");
            _db.SetSetting("update_repo", repo);
        }
        if (autoUpdate is not null) _db.SetSetting("auto_update", autoUpdate.Value ? "true" : "false");
        if (window is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(window, @"^\d{2}:\d{2}-\d{2}:\d{2}$"))
                throw new ArgumentException("window must look like 02:00-04:00");
            _db.SetSetting("update_window", window);
        }
        Audit.Add(_db, actor, "server_update.configure", $"repo={Repo} auto={AutoUpdate} window={Window}");
    }

    public async Task<object> CheckAsync(CancellationToken ct)
    {
        _lastCheckAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        try
        {
            var http = _httpFactory.CreateClient("github");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("SchoolBell-Server", Util.ServerVersion));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 404) throw new InvalidOperationException($"no published release in {Repo}");
            resp.EnsureSuccessStatusCode();
            var rel = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
            string? Asset(string name) => rel["assets"]?.AsArray()
                .FirstOrDefault(a => a?["name"]?.GetValue<string>() == name)?["browser_download_url"]?.GetValue<string>();
            var manifestUrl = Asset("release-manifest.json");
            var sigUrl = Asset("release-manifest.json.sig");
            var tarUrl = Asset("schoolbell-server-linux-x64.tar.gz");
            if (manifestUrl is null || sigUrl is null || tarUrl is null)
                throw new InvalidOperationException("the latest release lacks release-manifest.json, its .sig, or the server tarball");
            var manifestText = await http.GetStringAsync(manifestUrl, ct);
            var sig = await http.GetStringAsync(sigUrl, ct);
            var m = _trust.Verify(manifestText, sig) ?? throw new InvalidOperationException(
                "the latest release's signature does NOT verify against the pinned publisher key — not offered");
            int cmp = ReleaseSignature.CompareVersions(m.Version, Util.ServerVersion);
            _latest = new JsonObject
            {
                ["version"] = m.Version, ["commit"] = m.Commit, ["created_at"] = m.CreatedAt,
                ["tag"] = rel["tag_name"]?.GetValue<string>(), ["html_url"] = rel["html_url"]?.GetValue<string>(),
                ["manifest_url"] = manifestUrl, ["signature_url"] = sigUrl, ["tarball_url"] = tarUrl,
                ["tarball_sha256"] = m.Files.TryGetValue("server/schoolbell-server-linux-x64.tar.gz", out var f) ? f.Sha256 : null,
                ["newer"] = cmp > 0, ["verified"] = true,
            };
            _lastError = null;
            _log.LogInformation("update check: latest {Version} ({State})", m.Version, cmp > 0 ? "newer — available" : "not newer");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _lastError = e.Message;
            _log.LogWarning("server update check failed: {Error}", e.Message);
        }
        return Status();
    }

    public object Status()
    {
        JsonNode? updaterStatus = null;
        var statusPath = Path.Combine(UpdateDir, "status.json");
        if (File.Exists(statusPath)) { try { updaterStatus = JsonNode.Parse(File.ReadAllText(statusPath)); } catch (JsonException) { } }
        return new
        {
            current_version = Util.ServerVersion,
            repo = Repo, auto_update = AutoUpdate, window = Window,
            last_check_at = _lastCheckAt, last_error = _lastError,
            publisher_key = _trust.PublicKeyPem is not null,
            latest = _latest,
            request_pending = File.Exists(Path.Combine(UpdateDir, "request.json")),
            updater = updaterStatus,
        };
    }

    /// Records the request for the root updater; the web app never replaces its own files.
    public (int Status, string? Detail) RequestApply(string actor, string? version)
    {
        if (_latest is null || _latest["newer"]?.GetValue<bool>() != true)
            return (409, "no verified newer release is known — run a check first");
        if (version is not null && version != _latest["version"]?.GetValue<string>())
            return (409, "that version is not the latest verified release");
        Directory.CreateDirectory(UpdateDir);
        var req = new JsonObject
        {
            ["version"] = _latest["version"]?.GetValue<string>(),
            ["manifest_url"] = _latest["manifest_url"]?.GetValue<string>(),
            ["signature_url"] = _latest["signature_url"]?.GetValue<string>(),
            ["tarball_url"] = _latest["tarball_url"]?.GetValue<string>(),
            ["requested_by"] = actor,
            ["requested_at"] = _time.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        var tmp = Path.Combine(UpdateDir, "request.json.tmp");
        File.WriteAllText(tmp, req.ToJsonString());
        File.Move(tmp, Path.Combine(UpdateDir, "request.json"), overwrite: true);
        Audit.Add(_db, actor, "server_update.request", $"version={req["version"]}");
        _log.LogWarning("server update to {Version} requested by {Actor}; the root updater takes over", req["version"], actor);
        return (202, null);
    }

    /// Hourly: check every 6 h; with auto_update on, request the update inside the window when
    /// nothing is playing. The school's normal schedule decides the window, not the developer.
    public async Task PeriodicAsync(Func<bool> busy, CancellationToken ct)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (now - _lastCheckAt >= 6 * 3600_000L) await CheckAsync(ct);
        if (!AutoUpdate || _latest?["newer"]?.GetValue<bool>() != true || busy()) return;
        if (File.Exists(Path.Combine(UpdateDir, "request.json"))) return;
        var tzId = _db.GetSetting("school_tz");
        var zone = string.IsNullOrEmpty(tzId) ? null : NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(tzId);
        if (zone is null) return;   // never guess the school's local time
        var local = NodaTime.Instant.FromUnixTimeMilliseconds(now).InZone(zone).TimeOfDay;
        var parts = Window.Split('-');
        var start = NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm").Parse(parts[0]).Value;
        var end = NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm").Parse(parts[1]).Value;
        bool inWindow = start <= end ? local >= start && local < end : local >= start || local < end;
        if (inWindow) RequestApply("auto-update", null);
    }
}

public sealed class UpdatesHost : BackgroundService
{
    private readonly ClientUpdateService _client;
    private readonly ServerUpdateService _server;
    private readonly AnnouncementsService _announcements;
    private readonly ILogger<UpdatesHost> _log;

    public UpdatesHost(ClientUpdateService client, ServerUpdateService server, AnnouncementsService announcements, ILogger<UpdatesHost> log)
    {
        _client = client; _server = server; _announcements = announcements; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _client.ImportFromBundle();
        long lastServerCheck = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { _client.Tick(); } catch (Exception e) { _log.LogError(e, "client rollout tick failed"); }
            if (Environment.TickCount64 - lastServerCheck > 3600_000 || lastServerCheck == 0)
            {
                lastServerCheck = Environment.TickCount64;
                try { await _server.PeriodicAsync(() => _announcements.ActiveLiveCount() > 0, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning("update check pass failed: {Error}", e.Message); }
            }
        }
    }
}

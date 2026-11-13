using System.Text.Json;
using NodaTime;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record PublishOutcome(int Status, string? Detail, PublishResult? Result = null);

public sealed class PublishResult
{
    public long Revision { get; init; }
    public long HorizonStart { get; init; }
    public long HorizonEnd { get; init; }
    public long ExpiresAt { get; init; }
    public int OccurrenceCount { get; init; }
    public int DevicesNotified { get; init; }
    public CompileReport Report { get; init; } = new();
}

/// Publish = compile next 7 days -> sign manifest -> store revision -> enqueue schedule.manifest
/// into every non-revoked device's outbox (offline devices receive it on resync).
public sealed class PublishService
{
    private readonly object _publishGate = new();

    public sealed record PublishedRules(string SchoolTz, List<CompileTemplate> Templates,
        Dictionary<string, long?> Overrides, Dictionary<string, string> Holidays, List<SoundRef> Sounds);

    private PublishedRules CaptureRules() => new(SchoolTz ?? "", _templates.LoadCompileTemplates(),
        _templates.LoadOverrides(), _templates.LoadHolidays(), _sounds.List().Select(s =>
            new SoundRef(s.Id, s.Sha256, $"/api/sounds/{s.Id}?sha={s.Sha256}")).ToList());

    private void FreezeSounds(PublishedRules rules)
    {
        var dir = Path.Combine(_cfg.SoundsDir, "published");
        Directory.CreateDirectory(dir);
        foreach (var sound in rules.Sounds)
        {
            var dest = Path.Combine(dir, sound.Sha256 + ".wav");
            if (File.Exists(dest)) continue;
            var source = _sounds.ResolveFile(sound.Id);
            if (source is null || source.Value.Asset.Sha256 != sound.Sha256)
                throw new IOException($"published sound '{sound.Id}' is unavailable");
            var bytes = File.ReadAllBytes(source.Value.Path);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != sound.Sha256) throw new IOException("sound changed while publishing; retry publish");
            File.WriteAllBytes(dest + ".tmp", bytes);
            File.Move(dest + ".tmp", dest, overwrite: true);
        }
    }

    /// Refresh once per school day from the rules explicitly published by the operator.
    /// Never activates draft edits or creates a school's first schedule automatically.
    public PublishOutcome? RenewIfNeeded()
    {
        lock (_publishGate)
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT manifest_json, rules_json FROM published_revisions ORDER BY revision DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var latest = JsonSerializer.Deserialize<ScheduleManifestPayload>(reader.GetString(0), ControlEnvelope.Json)!;
            var rulesJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            reader.Close();
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(latest.SchoolTz);
            if (zone is null) return new(409, "published timezone is invalid; publish the schedule again");
            var today = Instant.FromDateTimeOffset(_time.GetUtcNow()).InZone(zone).Date;
            var wantedEnd = today.PlusDays(Protocol.ManifestHorizonDays).AtStartOfDayInZone(zone).ToInstant();
            bool needsRenewal = latest.HorizonEnd < wantedEnd.ToUnixTimeMilliseconds();
            if (!needsRenewal && rulesJson is not null) return null;
            PublishedRules rules;
            if (rulesJson is null)
            {
                // Older releases did not save rules with a publish. Adopt the saved rules only
                // when they reproduce that published horizon, otherwise require explicit Publish.
                rules = CaptureRules();
                var from = Instant.FromUnixTimeMilliseconds(latest.HorizonStart).InZone(zone).Date;
                var to = Instant.FromUnixTimeMilliseconds(latest.HorizonEnd - 1).InZone(zone).Date;
                var check = ScheduleCompiler.Compile(zone, from, to, rules.Templates, rules.Overrides, rules.Holidays, latest.ExpiresAt);
                if (rules.SchoolTz != latest.SchoolTz ||
                    CanonicalJson.Serialize(check.Occurrences) != CanonicalJson.Serialize(latest.Occurrences) ||
                    !rules.Sounds.All(s => latest.Sounds.Any(old => old.Id == s.Id && old.Sha256 == s.Sha256)))
                    return new(409, "automatic renewal needs one explicit Publish: saved rules differ from the legacy published schedule");
                FreezeSounds(rules);
                conn.Run("UPDATE published_revisions SET rules_json = $rules WHERE revision = $r",
                    ("$rules", JsonSerializer.Serialize(rules, ControlEnvelope.Json)), ("$r", latest.Revision));
            }
            else rules = JsonSerializer.Deserialize<PublishedRules>(rulesJson, ControlEnvelope.Json)!;
            if (!needsRenewal) return null;
            var outcome = PublishLocked("schedule-renewal", rules);
            if (outcome.Status == 200)
                _log.LogInformation("automatically renewed published schedule: revision {Revision}, expires {ExpiresAt}",
                    outcome.Result!.Revision, outcome.Result.ExpiresAt);
            return outcome;
        }
    }
    private readonly Db _db;
    private readonly ServerConfig _cfg;
    private readonly TimeProvider _time;
    private readonly TemplatesService _templates;
    private readonly DevicesService _devices;
    private readonly SoundService _sounds;
    private readonly OutboxService _outbox;
    private readonly Microsoft.Extensions.Logging.ILogger<PublishService> _log;

    public PublishService(Db db, ServerConfig cfg, TimeProvider time, TemplatesService templates,
        DevicesService devices, SoundService sounds, OutboxService outbox,
        Microsoft.Extensions.Logging.ILogger<PublishService> log)
    {
        _db = db; _cfg = cfg; _time = time; _templates = templates;
        _devices = devices; _sounds = sounds; _outbox = outbox; _log = log;
    }

    public string? SchoolTz => _db.GetSetting("school_tz") is string s && s.Length > 0 ? s : _cfg.SchoolTz;

    public long? LatestRevision()
    {
        using var conn = _db.Open();
        var v = conn.Scalar("SELECT COALESCE(MAX(revision), 0) FROM published_revisions");
        return Convert.ToInt64(v ?? 0);
    }

    /// Approximate size on the wire of the latest schedule.manifest (manifest + envelope), 0 if none.
    public long LatestManifestBytes()
    {
        using var conn = _db.Open();
        var v = conn.Scalar("SELECT LENGTH(CAST(manifest_json AS BLOB)) FROM published_revisions ORDER BY revision DESC LIMIT 1");
        return v is null or DBNull ? 0 : Convert.ToInt64(v) + 256;
    }

    public PublishOutcome Publish(string actor)
    {
        lock (_publishGate)
        {
            var rules = CaptureRules();
            if (!string.IsNullOrEmpty(rules.SchoolTz)) FreezeSounds(rules);
            return PublishLocked(actor, rules);
        }
    }

    private PublishOutcome PublishLocked(string actor, PublishedRules rules)
    {
        var tzId = rules.SchoolTz;
        if (string.IsNullOrEmpty(tzId))
            return new(409, "timezone not configured");
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(tzId);
        if (zone is null) return new(409, $"timezone '{tzId}' is not a valid IANA timezone");

        var now = _time.GetUtcNow();
        var today = Instant.FromDateTimeOffset(now).InZone(zone).Date;
        var horizonStart = today.AtStartOfDayInZone(zone).ToInstant();
        var horizonEnd = today.PlusDays(Protocol.ManifestHorizonDays).AtStartOfDayInZone(zone).ToInstant();
        long expiresAt = ScheduleCompiler.ToUnixMs(horizonEnd) + 3_600_000; // +1 h grace

        var compiled = ScheduleCompiler.Compile(zone, today, today.PlusDays(Protocol.ManifestHorizonDays - 1),
            rules.Templates, rules.Overrides, rules.Holidays, expiresAt);

        var key = SigningKey.Load(_cfg, TrustPin.Get(_db));
        var keyRef = "sha256:" + Util.Sha256Hex(Convert.ToHexString(key));

        long revision;
        ScheduleManifestPayload manifest;
        using (var conn = _db.Open())
        {
            revision = Convert.ToInt64(conn.Scalar("SELECT COALESCE(MAX(revision), 0) FROM published_revisions") ?? 0) + 1;
            manifest = new ScheduleManifestPayload
            {
                Revision = revision,
                SchoolTz = tzId,
                GeneratedAt = now.ToUnixTimeMilliseconds(),
                HorizonStart = ScheduleCompiler.ToUnixMs(horizonStart),
                HorizonEnd = ScheduleCompiler.ToUnixMs(horizonEnd),
                ExpiresAt = expiresAt,
                Sounds = rules.Sounds,
                Occurrences = compiled.Occurrences,
            };
            manifest.Signature = ManifestSigner.Sign(manifest, key);

            var size = System.Text.Encoding.UTF8.GetByteCount(
                ControlEnvelope.Create(Protocol.TScheduleManifest, PayloadSerializer.Node(manifest)).ToJson());
            if (size > Protocol.MaxServerMessageBytes)
                return new(409, $"the next 7 days hold {compiled.Occurrences.Count} bells ({size / 1024} KB), more than " +
                    $"endpoints accept ({Protocol.MaxServerMessageBytes / 1024} KB). Remove duplicate periods or unused schedules.");

            using var tx = conn.BeginTransaction();
            conn.Run(tx, """
                INSERT INTO published_revisions(revision, manifest_json, signing_key_ref, published_at, published_by, rules_json)
                VALUES ($r, $m, $k, $t, $p, $rules)
                """,
                ("$r", revision), ("$m", CanonicalJson.Serialize(manifest)), ("$k", keyRef),
                ("$t", now.ToUnixTimeMilliseconds()), ("$p", actor),
                ("$rules", JsonSerializer.Serialize(rules, ControlEnvelope.Json)));
            foreach (var o in compiled.Occurrences)
                conn.Run(tx, """
                    INSERT INTO occurrences(revision, occurrence_id, starts_at, bell_name, kind, sound_id,
                        volume_policy, duration_s, target_key, template_name, local_date, local_hm)
                    VALUES ($r, $o, $s, $b, $k2, $so, $vp, $du, $tk, $tn, $ld, $lh)
                    """,
                    ("$r", revision), ("$o", o.OccurrenceId), ("$s", o.StartsAt), ("$b", o.BellName),
                    ("$k2", o.Kind), ("$so", o.SoundId), ("$vp", o.VolumePolicy), ("$du", o.DurationS),
                    ("$tk", OccurrenceIds.TargetKey(o.Target)), ("$tn", o.ScheduleTemplate),
                    ("$ld", o.LocalDate), ("$lh", o.LocalHm));
            tx.Commit();
        }

        if (!string.IsNullOrEmpty(_db.GetSetting("schedule_renewal_error"))) _db.SetSetting("schedule_renewal_error", "");
        var targets = _devices.NonRevokedDevices();
        var envelope = ControlEnvelope.Create(Protocol.TScheduleManifest, PayloadSerializer.Node(manifest));
        foreach (var (deviceId, _) in targets)
            _outbox.Enqueue(deviceId, envelope);

        Audit.Add(_db, actor, "schedule.publish",
            $"revision={revision} occurrences={compiled.Occurrences.Count} devices={targets.Count} " +
            $"dst_skipped={compiled.Report.SkippedDst.Count} dst_ambiguous={compiled.Report.AmbiguousFirstMapping.Count}");
        foreach (var sk in compiled.Report.SkippedDst)
            _log.LogWarning("publish report: skipped {Kind} bell '{Bell}' on {Date} {Hm} ({Template}) — nonexistent local time in {Tz}",
                sk.Kind, sk.BellName, sk.LocalDate, sk.LocalHm, sk.TemplateName, tzId);

        return new(200, null, new PublishResult
        {
            Revision = revision,
            HorizonStart = manifest.HorizonStart,
            HorizonEnd = manifest.HorizonEnd,
            ExpiresAt = manifest.ExpiresAt,
            OccurrenceCount = compiled.Occurrences.Count,
            DevicesNotified = targets.Count,
            Report = compiled.Report,
        });
    }

    public Outcome PreviewManifestScoped(string tzId, string from, string to, out CompileResult? preview) =>
        _templates.Preview(tzId, from, to, out preview);
}

/// Manifest signing key lives only on the server (DataDir/keys/manifest.key, mode 0600).
/// It is derived from the per-school trust pin with the contracts KDF so devices can verify HMAC
/// signatures with DeriveDeviceVerifyKey(trust_pin) (see protocol ambiguity note in HANDOFF).
public static class SigningKey
{
    /// The file is created once (0600) as DeriveDeviceVerifyKey(trust_pin) so device-side HMAC
    /// verification with the trust pin succeeds; thereafter the file is the canonical key.
    public static byte[] Load(ServerConfig cfg, string trustPinHex)
    {
        Directory.CreateDirectory(cfg.KeysDir);
        if (!File.Exists(cfg.ManifestKeyPath))
        {
            var derived = ManifestSigner.DeriveDeviceVerifyKey(trustPinHex);
            File.WriteAllText(cfg.ManifestKeyPath, Convert.ToHexString(derived).ToLowerInvariant());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(cfg.ManifestKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // on Windows the installer grants restrictive ACLs
        }
        return Convert.FromHexString(File.ReadAllText(cfg.ManifestKeyPath).Trim());
    }
}

public static class TrustPin
{
    /// Per-school value stored in settings, created once on first start (32 random bytes hex).
    public static string Get(Db db)
    {
        using var conn = db.Open();
        var pin = Db.GetSetting(conn, "server_trust_pin");
        if (pin is null)
        {
            pin = Util.RandomHex(32);
            Db.SetSetting(conn, "server_trust_pin", pin);
        }
        return pin;
    }
}

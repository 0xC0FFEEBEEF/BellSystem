using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Service;

/// Server-pushed client updates (PROTOCOL §13). Trust comes only from the publisher key pinned
/// at install time ({app}\release-signing.pub.pem, Administrators-only): the signed
/// release-manifest.json must verify, name this version, and list the installer with the sha256
/// of the downloaded bytes. The install runs through update-runner.ps1, which waits for the new
/// service to reconnect and reinstalls the previous version if it does not (automatic rollback).
public sealed class ClientUpdater : IClientUpdater
{
    private readonly ClientConfig _cfg;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string _installDir;
    private readonly string _updatesDir;
    private Func<bool> _quiet = () => true;

    public string CurrentVersion { get; } = AppVersion();

    public ClientUpdater(ClientConfig cfg, HttpClient http, ILogger log)
    {
        _cfg = cfg; _http = http; _log = log;
        _installDir = AppContext.BaseDirectory;
        _updatesDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfg.DataDir))!, "updates");
    }

    public void SetQuietCheck(Func<bool> quiet) => _quiet = quiet;

    public static string AppVersion()
    {
        var v = typeof(ClientUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }

    public async Task HandleOfferAsync(UpdateOfferPayload offer, Func<UpdateStatusPayload, Task> report, CancellationToken ct)
    {
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Task Report(string state, string? error = null) => report(new UpdateStatusPayload(offer.Version, state, error, Now()));

        var keyPath = Path.Combine(_installDir, "release-signing.pub.pem");
        if (!File.Exists(keyPath)) { await Report("failed", "no_publisher_key"); return; }
        var manifest = ReleaseSignature.ParseVerified(offer.ReleaseManifest, offer.ReleaseSignature, await File.ReadAllTextAsync(keyPath, ct));
        if (manifest is null) { await Report("failed", "bad_signature"); return; }
        if (manifest.Version != offer.Version) { await Report("failed", "version_mismatch"); return; }
        int cmp = ReleaseSignature.CompareVersions(manifest.Version, CurrentVersion);
        if (cmp == 0) { await Report("installed"); return; }
        if (cmp < 0 && !offer.AllowDowngrade) { await Report("failed", "downgrade_refused"); return; }
        if (!manifest.Files.TryGetValue(offer.ArtifactPath, out var file)) { await Report("failed", "artifact_not_in_manifest"); return; }
        await Report("received");

        // staged rollout + never during a bell or an announcement
        var deadline = DateTimeOffset.UtcNow.AddHours(6);
        while (Now() < offer.NotBefore || !_quiet())
        {
            if (DateTimeOffset.UtcNow > deadline) { await Report("failed", "no_quiet_window"); return; }
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }

        var dir = Path.Combine(_updatesDir, Sanitize(manifest.Version));
        Directory.CreateDirectory(dir);
        var setup = Path.Combine(dir, "SchoolBellSetup.exe");
        await Report("downloading");
        var part = setup + ".part";
        try
        {
            using var resp = await _http.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) { await Report("failed", $"download_http_{(int)resp.StatusCode}"); return; }
            await using (var fs = File.Create(part))
                await resp.Content.CopyToAsync(fs, ct);
        }
        catch (HttpRequestException e) { await Report("failed", "download_failed"); _log.LogWarning("update download failed: {Error}", e.Message); return; }

        string sha;
        await using (var fs = File.OpenRead(part)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (sha != file.Sha256.ToLowerInvariant() || new FileInfo(part).Length != file.Bytes)
        {
            File.Delete(part);
            await Report("failed", "hash_mismatch");
            return;
        }
        File.Move(part, setup, overwrite: true);
        await Report("verified");

        // previous installer for rollback (the installer saves a copy of itself after each install)
        var installed = Path.Combine(_updatesDir, "installed-setup.exe");
        var previous = Path.Combine(_updatesDir, "previous-setup.exe");
        if (File.Exists(installed)) File.Copy(installed, previous, overwrite: true);

        var runner = Path.Combine(_installDir, "update-runner.ps1");
        if (!File.Exists(runner)) { await Report("failed", "runner_missing"); return; }
        var result = Path.Combine(_updatesDir, "result.json");
        try { File.Delete(result); } catch (IOException) { }

        await Report("installing");
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _updatesDir,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", runner,
                     "-Setup", setup, "-Previous", File.Exists(previous) ? previous : "", "-FromVersion", CurrentVersion,
                     "-ToVersion", manifest.Version, "-DataDir", _cfg.DataDir, "-ResultFile", result })
            psi.ArgumentList.Add(a);
        Process.Start(psi);
        _log.LogInformation("client update {Version}: update runner started; this service will be replaced", manifest.Version);
    }

    public UpdateStatusPayload? TakeCompletedResult()
    {
        var result = Path.Combine(_updatesDir, "result.json");
        if (!File.Exists(result)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<UpdateStatusPayload>(File.ReadAllText(result), ControlEnvelope.Json);
            File.Delete(result);
            return payload;
        }
        catch (Exception) { return null; }
    }

    private static string Sanitize(string v) => new(v.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());
}

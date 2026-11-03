using System.Text;
using Microsoft.Extensions.Logging;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

/// Fake TimeProvider: settable wall clock + monotonic stamp (1 tick = 1 ms).
/// CreateTimer uses REAL timers so agent loops still make progress if used.
public sealed class FakeTimeProvider : TimeProvider
{
    private long _wallMs;
    private long _stampMs;

    public FakeTimeProvider(long wallMs = 1_793_700_000_000, long stampMs = 5_000_000)
    {
        _wallMs = wallMs; _stampMs = stampMs;
    }

    public void SetWallMs(long ms) => _wallMs = ms;
    public void AdvanceStamp(long ms) => _stampMs += ms;
    public void AdvanceWall(long ms) => _wallMs += ms;

    public override DateTimeOffset GetUtcNow() =>
        DateTimeOffset.FromUnixTimeMilliseconds(_wallMs);

    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _stampMs;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var cts = new CancellationTokenSource();
        var linked = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (dueTime <= TimeSpan.Zero) { callback(state); return; }
                await Task.Delay(dueTime, linked);
                if (!linked.IsCancellationRequested) callback(state);
            }
            catch (OperationCanceledException) { }
        });
        return new SimpleTimer(cts);
    }

    private sealed class SimpleTimer : ITimer
    {
        private readonly CancellationTokenSource _cts;
        public SimpleTimer(CancellationTokenSource cts) => _cts = cts;
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => _cts.Cancel();
        public void Dispose(bool disposing) { if (disposing) _cts.Dispose(); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

public static class TestDirs
{
    public static string Create(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), "schoolbell-tests", label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}

public sealed class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = [];
    private readonly string _name;
    public CapturingLogger(string name = "test") => _name = name;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Messages) Messages.Add(formatter(state, exception));
    }
}

public static class ManifestTestKit
{
    public const string TrustPinHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    public static byte[] DeviceVerifyKey() => ManifestSigner.DeriveDeviceVerifyKey(TrustPinHex);

    /// Mirrors the live server (PublishService.SigningKey): the signing key IS the
    /// device verify key derived from the trust pin.
    public static ScheduleManifestPayload BuildManifest(long revision, long nowMs,
        IEnumerable<Occurrence>? occurrences = null, string? soundSha = null)
    {
        var manifest = new ScheduleManifestPayload
        {
            Revision = revision,
            SchoolTz = "America/Chicago",
            GeneratedAt = nowMs,
            HorizonStart = nowMs - 12 * 3_600_000,
            HorizonEnd = nowMs + 7 * 86_400_000,
            ExpiresAt = nowMs + 7 * 86_400_000 + 3_600_000,
            Sounds = [new SoundRef("std-bell", soundSha ?? new string('0', 64), "/api/sounds/std-bell")],
            Occurrences = occurrences?.ToList() ?? [],
        };
        manifest.Signature = ManifestSigner.Sign(manifest, DeviceVerifyKey());
        return manifest;
    }

    public static Occurrence Bell(string id, long startsAt, long expiresAt,
        string soundId = "std-bell", TargetScope? target = null, int durationS = 3) =>
        new(id, startsAt, "Period " + id[^1], "start", soundId, "inherit", durationS,
            target ?? new TargetScope(All: true, null, null),
            "Regular Day", "2026-11-04", "08:30", expiresAt);
}

using System.Text;
using Microsoft.Extensions.Logging;

namespace SchoolBell.Server;

/// The server's own log for Admin: the last lines in memory plus one file per day (kept 14 days)
/// under DataDir/logs. Normal mode keeps SchoolBell information and above (Microsoft.* warnings);
/// Verbose adds SchoolBell debug detail. The flag is a file, so it survives restarts and needs no
/// database.
public sealed class LogBuffer
{
    public const int Capacity = 10_000;
    public const int KeepDays = 14;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private string? _dir;
    private string? _fileDate;
    private StreamWriter? _file;
    private volatile bool _verbose;

    public bool Verbose => _verbose;
    public string? Directory => _dir;

    /// The school's timezone (IANA id), so log times match the school's clocks rather than the
    /// server's (often UTC). Looked up at most once a minute.
    public Func<string?>? SchoolTimeZone { get; set; }
    private TimeZoneInfo? _zone;
    private long _zoneCheckedAt;
    private int _zoneRefreshing;

    public DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        if (SchoolTimeZone is not null && Environment.TickCount64 - Interlocked.Read(ref _zoneCheckedAt) > 60_000
            && Interlocked.Exchange(ref _zoneRefreshing, 1) == 0)
        {
            try
            {
                Interlocked.Exchange(ref _zoneCheckedAt, Environment.TickCount64);
                var id = SchoolTimeZone();
                _zone = string.IsNullOrEmpty(id) ? null : TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                // not ready yet (startup, before the database): look again in a couple of seconds
                _zone = null;
                Interlocked.Exchange(ref _zoneCheckedAt, Environment.TickCount64 - 58_000);
            }
            finally { Interlocked.Exchange(ref _zoneRefreshing, 0); }
        }
        var zone = _zone;
        return zone is null ? now.ToLocalTime() : TimeZoneInfo.ConvertTime(now, zone);
    }

    /// Starts writing daily files; what was logged before this point is written first.
    public void EnableFiles(string dir)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(dir);
            _dir = dir;
            _verbose = File.Exists(Path.Combine(dir, "verbose.flag"));
            foreach (var line in _lines) WriteFileLocked(line);
        }
    }

    public void SetVerbose(bool on)
    {
        lock (_gate)
        {
            _verbose = on;
            if (_dir is null) return;
            var flag = Path.Combine(_dir, "verbose.flag");
            if (on) File.WriteAllText(flag, DateTimeOffset.Now.ToString("O"));
            else if (File.Exists(flag)) File.Delete(flag);
        }
    }

    public bool Accepts(string category, LogLevel level)
    {
        if (level == LogLevel.None) return false;
        if (category.StartsWith("Microsoft.Hosting.Lifetime", StringComparison.Ordinal)) return level >= LogLevel.Information;
        if (category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal))
            return level >= LogLevel.Warning;
        return level >= (_verbose ? LogLevel.Debug : LogLevel.Information);
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
            if (_dir is not null) WriteFileLocked(line);
        }
    }

    private void WriteFileLocked(string line)
    {
        try
        {
            var today = Now().ToString("yyyy-MM-dd");
            if (_file is null || _fileDate != today)
            {
                _file?.Dispose();
                _fileDate = today;
                _file = new StreamWriter(new FileStream(Path.Combine(_dir!, $"server-{today}.log"),
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                foreach (var old in System.IO.Directory.GetFiles(_dir!, "server-*.log"))
                    if (string.CompareOrdinal(Path.GetFileName(old), $"server-{Now().AddDays(-KeepDays):yyyy-MM-dd}.log") < 0)
                        try { File.Delete(old); } catch (IOException) { }
            }
            _file.WriteLine(line);
        }
        catch (Exception) { /* logging must never take the server down */ }
    }

    /// Newest lines last. level "warn" keeps warnings and errors only; q is a case-insensitive filter.
    public List<string> Tail(int count, string? level, string? q)
    {
        List<string> snapshot;
        lock (_gate) snapshot = [.. _lines];
        IEnumerable<string> sel = snapshot;
        if (level == "warn") sel = sel.Where(l => l.Contains(" WARN ", StringComparison.Ordinal) || l.Contains(" ERROR ", StringComparison.Ordinal)
                                               || l.Contains(" CRIT ", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(q)) sel = sel.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = sel.ToList();
        return list.Count <= count ? list : list.GetRange(list.Count - count, count);
    }

    /// Full text of the last `days` daily files (oldest first); the in-memory lines when files are off.
    public string Export(int days)
    {
        var sb = new StringBuilder();
        string[] files;
        lock (_gate)
        {
            if (_dir is null)
            {
                foreach (var l in _lines) sb.AppendLine(l);
                return sb.ToString();
            }
            files = System.IO.Directory.GetFiles(_dir, "server-*.log").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }
        foreach (var f in files.TakeLast(Math.Clamp(days, 1, KeepDays)))
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            sb.Append(r.ReadToEnd());
        }
        return sb.ToString();
    }
}

public sealed class LogBufferProvider(LogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(categoryName, buffer);
    public void Dispose() { }

    private sealed class BufferLogger(string category, LogBuffer buffer) : ILogger
    {
        private readonly string _short = category.StartsWith("SchoolBell.Server.", StringComparison.Ordinal)
            ? category["SchoolBell.Server.".Length..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => buffer.Accepts(category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRACE", LogLevel.Debug => "DEBUG", LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", _ => "CRIT",
            };
            var line = $"{buffer.Now():yyyy-MM-dd HH:mm:ss.fff zzz} {level} {_short}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + "    " + exception.ToString().Replace("\n", "\n    ");
            buffer.Add(line);
        }
    }
}

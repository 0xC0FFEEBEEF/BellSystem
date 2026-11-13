using Microsoft.Extensions.Logging;

namespace SchoolBell.Client.Desktop;

/// Small rolling file log in %LOCALAPPDATA%\SchoolBell\companion.log (no secrets ever pass
/// through the companion; it holds no credentials).
internal sealed class CompanionLog : ILogger
{
    private static readonly object Gate = new();
    private readonly string _path;

    public CompanionLog()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchoolBell");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "companion.log");
    }

    public string FilePath => _path;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception.ToString())}";
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }
}

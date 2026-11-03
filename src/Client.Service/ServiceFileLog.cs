using Microsoft.Extensions.Logging;

namespace SchoolBell.Client.Service;

/// Rolling diagnostic log at %ProgramData%\SchoolBell\logs\service.log (2 MB x 3), readable by
/// administrators only (inherits the folder ACL). Same discipline as the EventLog: no secrets.
public sealed class ServiceFileLogProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _gate = new();

    public ServiceFileLogProvider()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SchoolBell", "logs");
        try { Directory.CreateDirectory(dir); } catch (Exception) { }
        _path = Path.Combine(dir, "service.log");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 2_000_000)
                {
                    var two = _path + ".2";
                    if (File.Exists(_path + ".1")) File.Move(_path + ".1", two, overwrite: true);
                    File.Move(_path, _path + ".1", overwrite: true);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    private sealed class FileLogger(ServiceFileLogProvider owner, string category) : ILogger
    {
        private readonly string _short = category.StartsWith("SchoolBell.") ? category[11..] : category;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (_short.StartsWith("System.Net.Http")) return;
            owner.Write($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {LevelTag(logLevel)} {_short}: {formatter(state, exception)}" +
                        (exception is null ? "" : $" [{exception.GetType().Name}: {exception.Message}]"));
        }
        private static string LevelTag(LogLevel l) => l switch
        {
            LogLevel.Critical => "CRIT", LogLevel.Error => "ERR ", LogLevel.Warning => "WARN", _ => "INFO",
        };
    }
}

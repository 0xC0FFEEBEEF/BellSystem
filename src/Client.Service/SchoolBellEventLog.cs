using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SchoolBell.Client.Service;

/// ILoggerProvider writing operational events to the Windows EventLog with source
/// "SchoolBell". Logging discipline: message TEXT is what lands in the EventLog — the
/// service code must never pass device secrets, proofs or media tokens as data or text.
/// (Client.Core is written so its log calls never interpolate secrets; unit coverage:
/// FakeControlServerTests assert captured logs contain no secret/token strings.)
public sealed class SchoolBellEventLogProvider : ILoggerProvider
{
    public const string SourceName = "SchoolBell";

    public ILogger CreateLogger(string categoryName) => new EventLogLogger(categoryName);

    public void Dispose() { }

    private sealed class EventLogLogger : ILogger
    {
        private readonly string _category;
        private static readonly bool LogAvailable = EnsureSource();

        public EventLogLogger(string category) => _category = category;

        private static bool EnsureSource()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                if (!EventLog.SourceExists(SourceName))
                    EventLog.CreateEventSource(SourceName, "Application");
                return true;
            }
            catch (Exception)
            {
                return false; // source creation needs rights; console logging still active
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => LogAvailable && logLevel != LogLevel.None && logLevel != LogLevel.Trace;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"{_category}: {formatter(state, exception)}" +
                          (exception is null ? "" : $" [{exception.GetType().Name}]");
            if (message.Length > 30_000) message = message[..30_000];
            try
            {
                EventLog.WriteEntry(SourceName, message, logLevel switch
                {
                    LogLevel.Critical or LogLevel.Error => EventLogEntryType.Error,
                    LogLevel.Warning => EventLogEntryType.Warning,
                    _ => EventLogEntryType.Information,
                });
            }
            catch (Exception) { /* EventLog must never take the service down */ }
        }
    }
}

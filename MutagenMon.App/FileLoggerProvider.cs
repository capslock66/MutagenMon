using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>Minimal <see cref="ILoggerProvider"/> writing plain-text lines
/// to a file — no third-party logging library. Two sinks:
/// <list type="bullet">
/// <item>the primary log, at no path at all until <see cref="SetPrimaryLogPath"/>
/// is called once <c>config_mutagenmon.json</c>'s <c>LogPath</c> is known
/// (the app starts logging before that file is read — see
/// <see cref="App"/>). Deliberately no default/fallback path under the
/// app's own directory: nothing gets written to the primary sink at all
/// during that window, rather than creating a stray log file the user
/// never configured and doesn't want — same reasoning as never writing
/// next to the executable either (see below).</item>
/// <item>the Windows Application Event Log (source <c>"MutagenMon"</c>),
/// for every Critical entry, and for a non-Critical entry that failed to
/// reach the primary file. Deliberately not another file next to the
/// executable: a durable, always-present sink that doesn't depend on any
/// path this app resolves or on the app's own directory being writable —
/// exactly what's needed for a fatal failure, including one that happens
/// before the primary log's path is even known.</item>
/// </list>
/// Every level is written until <see cref="SetMinLevel"/> is called (once
/// <c>MinLogLevel</c> is known — same two-stage pattern as
/// <see cref="SetPrimaryLogPath"/>, and for the same reason: the fragile
/// early-startup window, before config is even read, must never silently
/// drop a line just because the eventually-configured level would have
/// excluded it).
/// Each write opens, appends, and closes the file — no persistent handle is
/// held between log calls, so there is nothing to flush or dispose when
/// reconfiguring or shutting down. Write failures are caught and reported
/// to <see cref="Debug"/> rather than propagated: a broken logger must
/// never crash the app.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const string EventLogSource = "MutagenMon";

    private readonly object _writeLock = new();
    private string? _primaryLogPath;
    private LogLevel _minLevel = LogLevel.Trace;

    public void SetPrimaryLogPath(string path)
    {
        lock (_writeLock)
        {
            _primaryLogPath = path;
        }
    }

    public void SetMinLevel(LogLevel level)
    {
        lock (_writeLock)
        {
            _minLevel = level;
        }
    }

    internal bool IsLevelEnabled(LogLevel level)
    {
        lock (_writeLock)
        {
            return level != LogLevel.None && level >= _minLevel;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        var line = FormatLine(categoryName, level, message, exception);
        lock (_writeLock)
        {
            // Primary path is null before config is loaded (SetPrimaryLogPath
            // not yet called) — that's not a write "failure" worth an event
            // log entry, just nothing to write to yet.
            var wroteToPrimary = _primaryLogPath is not null && TryAppend(_primaryLogPath, line);
            if (level == LogLevel.Critical)
                WriteToWindowsEventLog(line, EventLogEntryType.Error);
            else if (_primaryLogPath is not null && !wroteToPrimary)
                // The primary sink just failed for a non-Critical entry —
                // still worth a durable trace of that fact.
                WriteToWindowsEventLog($"Failed to write to primary log '{_primaryLogPath}'; see Debug output.", EventLogEntryType.Warning);
        }
    }

    /// <summary>Best-effort trace to the Windows Application event log —
    /// registering the event source requires local admin on first run only
    /// (subsequent writes don't); if that fails (e.g. a non-elevated
    /// install), this silently gives up rather than let the logger itself
    /// crash the app.</summary>
    internal static void WriteToWindowsEventLog(string message, EventLogEntryType entryType)
    {
        try
        {
            if (!EventLog.SourceExists(EventLogSource))
                EventLog.CreateEventSource(EventLogSource, "Application");
            EventLog.WriteEntry(EventLogSource, message, entryType);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FileLoggerProvider: failed writing to the Windows Event Log: {ex}");
        }
    }

    private static bool TryAppend(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FileLoggerProvider: failed writing to '{path}': {ex}");
            return false;
        }
    }

    private static string FormatLine(string categoryName, LogLevel level, string message, Exception? exception)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var levelTag = level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "FTL",
            _ => "???",
        };
        var line = $"{timestamp} [{levelTag}] {categoryName}: {message}{Environment.NewLine}";
        if (exception is not null)
            line += exception + Environment.NewLine;
        return line;
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsLevelEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            provider.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}

using System.IO;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>Minimal <see cref="ILoggerProvider"/> writing plain-text lines
/// to a file — no third-party logging library. Two sinks:
/// <list type="bullet">
/// <item>the primary log, every level, whose path is mutable via
/// <see cref="SetPrimaryLogPath"/> so it can be reconfigured once
/// <c>config_mutagenmon.json</c>'s <c>LOG_PATH</c> is known (the app starts
/// logging before that file is read — see <see cref="App"/>);</item>
/// <item>a fixed-location, Critical-only fallback file next to the
/// executable — deliberately not under the (possibly mutagen-synced, and
/// therefore occasionally locked) primary path, so a crash is never lost
/// to a transient write failure on the primary sink.</item>
/// </list>
/// Each write opens, appends, and closes the file — no persistent handle is
/// held between log calls, so there is nothing to flush or dispose when
/// reconfiguring or shutting down. Write failures are caught and reported
/// to <see cref="System.Diagnostics.Debug"/> (and, best-effort, to the
/// fallback file) rather than propagated: a broken logger must never crash
/// the app.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _fatalLogPath;
    private readonly object _writeLock = new();
    private string _primaryLogPath;

    public FileLoggerProvider(string initialPrimaryLogPath, string fatalLogPath)
    {
        _primaryLogPath = initialPrimaryLogPath;
        _fatalLogPath = fatalLogPath;
    }

    public void SetPrimaryLogPath(string path)
    {
        lock (_writeLock)
        {
            _primaryLogPath = path;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        var line = FormatLine(categoryName, level, message, exception);
        lock (_writeLock)
        {
            var wroteToPrimary = TryAppend(_primaryLogPath, line);
            if (level == LogLevel.Critical)
            {
                TryAppend(_fatalLogPath, line);
            }
            else if (!wroteToPrimary)
            {
                // The primary sink just failed for a non-Critical entry —
                // still worth a durable trace of that fact.
                TryAppend(_fatalLogPath, FormatLine("FileLoggerProvider", LogLevel.Warning,
                    $"Failed to write to primary log '{_primaryLogPath}'; see Debug output.", null));
            }
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
            System.Diagnostics.Debug.WriteLine($"FileLoggerProvider: failed writing to '{path}': {ex}");
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
        {
            line += exception + Environment.NewLine;
        }
        return line;
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}

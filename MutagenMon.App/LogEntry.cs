using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>One logged entry, as shown by the "Logs" tab
/// (<see cref="LogsView"/>) — distinct from <see cref="FileLoggerProvider"/>'s
/// own file-line formatting, which stays free text on disk.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    private static readonly char[] LineSeparators = { '\r', '\n' };

    /// <summary>Drives the Logs tab grid's red row highlighting.</summary>
    public bool IsErrorOrCritical => Level is LogLevel.Error or LogLevel.Critical;

    /// <summary>What the grid's Message column shows: <see cref="Message"/>
    /// collapsed to its first line, with an indicator appended when there's
    /// more — a multi-line <see cref="Message"/> (e.g. one with an appended
    /// exception, see <see cref="FileLoggerProvider"/>'s <c>FormatMessage</c>)
    /// would otherwise blow out the row height. The full text is still
    /// available in the detail panel below the grid.</summary>
    public string GridSummary
    {
        get
        {
            var lines = Message.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length <= 1 ? Message : $"{lines[0]} [+{lines.Length - 1} more line(s)]";
        }
    }
}

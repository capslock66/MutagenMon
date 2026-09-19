using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>One logged entry, as shown by the "Logs" tab
/// (<see cref="LogsView"/>) — distinct from <see cref="FileLoggerProvider"/>'s
/// own file-line formatting, which stays free text on disk.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);

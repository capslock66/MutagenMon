namespace MutagenMon.Core.Monitoring;

/// <summary>
/// Ports mutagenmonlib/remote/monitor.py: restart_mutagen()'s
/// `append_log(cfg('LOG_PATH') + '/restart.log', ...)` call (FR-13.4) — a
/// dedicated automatic-restart log, independent of the main application log
/// and of the conflict-resolution log (FR-14.3). Self-contained per call
/// (open/append/close, no persistent handle held between calls), matching
/// <see cref="Resolution.ResolveLogWriter"/>'s design.
/// </summary>
public sealed class RestartLogWriter
{
    private readonly string _logFilePath;

    public RestartLogWriter(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, "restart.log");
    }

    /// <summary>Appends one restart entry: the raw status snapshot that
    /// triggered it (this poll's full `mutagen sync list` output) followed by
    /// which of FR-13's three causes fired, for the named session.</summary>
    public void Append(string sessionName, string rawStatusSnapshot, string cause, DateTimeOffset timestampUtc)
    {
        var entry = $"[{timestampUtc:yyyy-MM-dd HH:mm:ss}]\n{rawStatusSnapshot}\n{cause}: {sessionName}\n";
        File.AppendAllText(_logFilePath, entry);
    }
}

namespace MutagenMon.Core.Resolution;

/// <summary>
/// Ports mutagenmonlib/local/file.py: resolve_log() (FR-9.7) — a dedicated
/// conflict-resolution log, independent of the main application log
/// (FR-14.3). Self-contained per call (open/append/close, no persistent
/// handle held between calls) to match the primary log's design — see
/// requirements/05-wpf-migration-notes.md §7.
/// </summary>
public sealed class ResolveLogWriter
{
    private readonly string _logFilePath;

    public ResolveLogWriter(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, "resolve.log");
    }

    public void Append(string sessionName, string urlAlpha, string urlBeta, string fileName, string method, bool automatic, DateTimeOffset timestampUtc)
    {
        var entry = $"[{timestampUtc:yyyy-MM-dd HH:mm:ss}]{(automatic ? " [AUTO]" : "")}\n" +
                    $"{sessionName}\n{urlAlpha}\n{urlBeta}\n{fileName}\n{method}\n";
        File.AppendAllText(_logFilePath, entry);
    }
}

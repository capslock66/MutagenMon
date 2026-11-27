using System.Diagnostics;
using System.Globalization;

namespace MutagenMon.Core.Logging;

/// <summary>
/// Removes the entries older than a retention period from the single
/// <c>mutagenMon.log</c> file. The file is one append-only text file, so
/// "old logs" means old entries: every entry starts with a local timestamp
/// (<c>yyyy-MM-dd HH:mm:ss.fff zzz</c>, see <c>FileLoggerProvider</c>) and
/// the lines that follow it without such a prefix (an exception's stack
/// trace) belong to it.
/// </summary>
public static class LogFilePurger
{
    /// <summary>Length of the leading <c>yyyy-MM-dd</c> date of an entry's timestamp.</summary>
    private const int DatePrefixLength = 10;

    /// <summary>Smallest line that can start an entry: the date, a space,
    /// then at least the start of the time of day.</summary>
    private const int MinTimestampedLineLength = DatePrefixLength + 1;

    /// <summary>Retention applied when the configured value is not
    /// strictly positive (never "keep nothing").</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>Upper bound of the retention (100 years). Beyond it the
    /// cutoff date would fall before <see cref="DateTime.MinValue"/> and
    /// <c>AddDays</c> would throw; nothing is that old anyway.</summary>
    public const int MaxRetentionDays = 36500;

    /// <summary>Returns <paramref name="configuredDays"/> capped to
    /// <see cref="MaxRetentionDays"/>, or <see cref="DefaultRetentionDays"/>
    /// when it is zero or negative.</summary>
    public static int NormalizeRetentionDays(int configuredDays) =>
        configuredDays > 0 ? Math.Min(configuredDays, MaxRetentionDays) : DefaultRetentionDays;

    /// <summary>Deletes from <paramref name="logFilePath"/> every entry
    /// dated before <c>today - retentionDays</c> (calendar days, local
    /// time). Returns the number of lines removed; 0 when the file does not
    /// exist or nothing is old enough, and -1 when the file could not be
    /// rewritten (e.g. held open by another program; the original is left
    /// untouched).
    /// The caller must make sure nothing appends to the file meanwhile.</summary>
    public static int PurgeOlderThan(string logFilePath, int retentionDays, DateTime today)
    {
        if (!File.Exists(logFilePath))
            return 0;

        var oldestKeptDate = today.Date.AddDays(-NormalizeRetentionDays(retentionDays));

        // Written next to the log, then swapped in: a crash mid-purge leaves
        // the original untouched rather than a truncated log.
        var temporaryPath = logFilePath + ".purge.tmp";
        var removedLines = 0;
        try
        {
            // Inside the try: a file held open exclusively fails right here,
            // and that must be reported (-1), not mistaken for "nothing to purge".
            if (!StartsWithOldEntry(logFilePath, oldestKeptDate))
                return 0;

            using (var reader = new StreamReader(logFilePath))
            using (var writer = new StreamWriter(temporaryPath, append: false))
            {
                // A file not starting with a timestamp (foreign content) is kept.
                var keepCurrentEntry = true;
                while (reader.ReadLine() is { } line)
                {
                    if (TryReadEntryDate(line, out var entryDate))
                        keepCurrentEntry = entryDate >= oldestKeptDate;

                    if (keepCurrentEntry)
                        writer.WriteLine(line);
                    else
                        removedLines++;
                }
            }

            File.Move(temporaryPath, logFilePath, overwrite: true);
            return removedLines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"LogFilePurger: failed purging '{logFilePath}': {ex}");
            TryDelete(temporaryPath);
            return -1;
        }
    }

    /// <summary>True when the first timestamped line of the file is older
    /// than <paramref name="oldestKeptDate"/>. The log is chronological, so
    /// this cheap check lets the daily run skip the full rewrite (and the
    /// lock held during it) when there is nothing to purge. Throws the I/O
    /// exceptions of opening the file; <see cref="PurgeOlderThan"/> handles them.</summary>
    private static bool StartsWithOldEntry(string logFilePath, DateTime oldestKeptDate)
    {
        using var reader = new StreamReader(logFilePath);
        while (reader.ReadLine() is { } line)
            if (TryReadEntryDate(line, out var entryDate))
                return entryDate < oldestKeptDate;
        return false;
    }

    private static bool TryReadEntryDate(string line, out DateTime entryDate)
    {
        entryDate = default;
        if (line.Length < MinTimestampedLineLength || line[DatePrefixLength] != ' ')
            return false;
        return DateTime.TryParseExact(
            line.AsSpan(0, DatePrefixLength), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out entryDate);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is overwritten by the next purge.
        }
    }
}

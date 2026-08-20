namespace MutagenMon.Core.ProfileWatch;

/// <summary>Injectable so <see cref="SessionProfileWatcher"/> is deterministic
/// and testable without touching a real filesystem (NFR-11).</summary>
public interface IFileTimestampProvider
{
    /// <summary>Null if the file does not exist or is not accessible.</summary>
    DateTimeOffset? GetLastWriteTimeUtc(string path);
}

public sealed class FileTimestampProvider : IFileTimestampProvider
{
    public DateTimeOffset? GetLastWriteTimeUtc(string path)
    {
        // Unlike Python's os.path.getmtime, File.GetLastWriteTimeUtc does not throw
        // for a missing file (it returns a 1601 sentinel) — check existence explicitly
        // to get the "file not found" signal the legacy code relies on catching.
        if (!File.Exists(path)) return null;
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

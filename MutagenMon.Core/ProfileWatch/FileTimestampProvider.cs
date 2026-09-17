namespace MutagenMon.Core.ProfileWatch;

/// <summary>Deliberately not sealed and <see cref="GetLastWriteTimeUtc"/> is
/// virtual so <see cref="SessionProfileWatcher"/> can be tested with an
/// override-based fake, deterministic and without touching a real
/// filesystem (NFR-11).</summary>
public class FileTimestampProvider
{
    /// <summary>Null if the file does not exist or is not accessible.</summary>
    public virtual DateTimeOffset? GetLastWriteTimeUtc(string path)
    {
        // File.GetLastWriteTimeUtc does not throw for a missing file (it returns
        // a 1601 sentinel) — check existence explicitly to get the "file not found"
        // signal the legacy code relies on catching.
        if (!File.Exists(path))
            return null;
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

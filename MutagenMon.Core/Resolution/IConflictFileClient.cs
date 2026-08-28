using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

/// <summary>
/// Abstraction over the local/SSH file operations FR-9 needs — stat, copy
/// between two endpoints, fetch/push a local working copy for the visual
/// merge tool, and invoking the merge tool itself. Kept as an interface (mirroring
/// <see cref="IMutagenCliClient"/>) so <see cref="ConflictResolutionService"/>
/// is testable with a fake, without a real ssh/scp/merge-tool on the test
/// machine.
/// </summary>
public interface IConflictFileClient
{
    /// <summary>Stats a file — via SSH for a remote endpoint, or directly via
    /// the filesystem for a local one.</summary>
    Task<FileStat> StatAsync(SessionEndpoint endpoint, string relativePath, CancellationToken cancellationToken);

    /// <summary>Copies <paramref name="relativePath"/> from
    /// <paramref name="source"/> to <paramref name="destination"/> — a direct
    /// local copy, a single scp hop if exactly one side is local, or a
    /// round-trip through a local temp file if both sides are SSH.</summary>
    Task CopyBetweenEndpointsAsync(
        SessionEndpoint source, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken);

    /// <summary>For a local endpoint, returns the real
    /// file path directly (the merge tool edits it in place); for an SSH
    /// endpoint, scp's it down to a local temp file (named after
    /// <paramref name="side"/>, 1 or 2) and returns that path.</summary>
    Task<string> FetchLocalCopyAsync(
        SessionEndpoint endpoint, string relativePath, int side, CancellationToken cancellationToken);

    /// <summary>Pushes
    /// <paramref name="localPath"/> to <paramref name="destination"/> — scp if
    /// SSH, a plain local copy otherwise.</summary>
    Task PushLocalFileAsync(
        string localPath, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken);

    /// <summary>Launches the configured MERGE_PATH tool
    /// with both local paths and waits for it to exit.</summary>
    Task RunMergeToolAsync(string localPath1, string localPath2, CancellationToken cancellationToken);
}

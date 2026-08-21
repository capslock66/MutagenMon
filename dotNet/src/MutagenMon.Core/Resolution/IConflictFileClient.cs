using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

/// <summary>
/// Abstraction over the local/SSH file operations FR-9 needs — stat, copy
/// between two endpoints, fetch/push a local working copy for the visual
/// merge tool, and invoking the merge tool itself. Ports
/// mutagenmonlib/remote/{resolve,ssh}.py; kept as an interface (mirroring
/// <see cref="IMutagenCliClient"/>) so <see cref="ConflictResolutionService"/>
/// is testable with a fake, without a real ssh/scp/merge-tool on the test
/// machine.
/// </summary>
public interface IConflictFileClient
{
    /// <summary>Ports get_size_time_ssh() / the local os.path.getsize+getmtime
    /// branch of resolve_single().</summary>
    Task<FileStat> StatAsync(SessionEndpoint endpoint, string relativePath, CancellationToken cancellationToken);

    /// <summary>Ports resolve(): copies <paramref name="relativePath"/> from
    /// <paramref name="source"/> to <paramref name="destination"/> — a direct
    /// local copy, a single scp hop if exactly one side is local, or a
    /// round-trip through a local temp file if both sides are SSH.</summary>
    Task CopyBetweenEndpointsAsync(
        SessionEndpoint source, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken);

    /// <summary>Ports make_diff_path(): for a local endpoint, returns the real
    /// file path directly (the merge tool edits it in place); for an SSH
    /// endpoint, scp's it down to a local temp file (named after
    /// <paramref name="side"/>, 1 or 2) and returns that path.</summary>
    Task<string> FetchLocalCopyAsync(
        SessionEndpoint endpoint, string relativePath, int side, CancellationToken cancellationToken);

    /// <summary>Ports the propagation half of visual_merge(): pushes
    /// <paramref name="localPath"/> to <paramref name="destination"/> — scp if
    /// SSH, a plain local copy otherwise.</summary>
    Task PushLocalFileAsync(
        string localPath, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken);

    /// <summary>Ports run_merge(): launches the configured MERGE_PATH tool
    /// with both local paths and waits for it to exit.</summary>
    Task RunMergeToolAsync(string localPath1, string localPath2, CancellationToken cancellationToken);
}

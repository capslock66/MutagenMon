using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

/// <summary>
/// Ports mutagenmonlib/remote/resolve.py: resolve() and visual_merge() — the
/// per-conflict resolution actions FR-9.2 offers. Batch assembly/guards live
/// in <see cref="ConflictBatchPlanner"/>; the UI loop (numbering, cancel,
/// re-presenting a conflict after a no-op merge) is a presentation concern
/// and lives in the App layer.
/// </summary>
public sealed class ConflictResolutionService
{
    private readonly IConflictFileClient _fileClient;
    private readonly ResolveLogWriter _resolveLog;

    public ConflictResolutionService(IConflictFileClient fileClient, ResolveLogWriter resolveLog)
    {
        _fileClient = fileClient;
        _resolveLog = resolveLog;
    }

    public Task<FileStat> StatAlphaAsync(PendingConflict conflict, CancellationToken cancellationToken) =>
        _fileClient.StatAsync(conflict.Alpha, conflict.FileName, cancellationToken);

    public Task<FileStat> StatBetaAsync(PendingConflict conflict, CancellationToken cancellationToken) =>
        _fileClient.StatAsync(conflict.Beta, conflict.FileName, cancellationToken);

    /// <summary>Ports resolve(): copies the winning side over the other and
    /// appends a resolve-log entry (FR-9.2/FR-9.7). <paramref name="automatic"/>
    /// is true when called from <see cref="AutoResolveEngine"/> (FR-10.2)
    /// rather than the manual resolution UI loop (FR-9).</summary>
    public async Task ResolveAsync(
        PendingConflict conflict, ConflictResolutionChoice choice, DateTimeOffset timestampUtc, CancellationToken cancellationToken,
        bool automatic = false)
    {
        var (source, destination) = choice == ConflictResolutionChoice.AWins
            ? (conflict.Alpha, conflict.Beta)
            : (conflict.Beta, conflict.Alpha);

        await _fileClient.CopyBetweenEndpointsAsync(source, destination, conflict.FileName, cancellationToken);
        _resolveLog.Append(
            conflict.SessionName, conflict.Alpha.Url, conflict.Beta.Url, conflict.FileName,
            choice == ConflictResolutionChoice.AWins ? "A wins" : "B wins", automatic, timestampUtc);
    }

    /// <summary>Ports the "fetch" half of visual_merge() — split out from
    /// running the tool and propagating the result so a caller (the App
    /// layer) can show the "connecting" indicator (FR-9.6) only around the
    /// actual remote I/O, not while the external merge tool has focus.</summary>
    public async Task<VisualMergePreparation> PrepareVisualMergeAsync(PendingConflict conflict, CancellationToken cancellationToken)
    {
        var localPath1 = await _fileClient.FetchLocalCopyAsync(conflict.Alpha, conflict.FileName, side: 1, cancellationToken);
        var localPath2 = await _fileClient.FetchLocalCopyAsync(conflict.Beta, conflict.FileName, side: 2, cancellationToken);
        var oldMtimeUtc1 = File.GetLastWriteTimeUtc(localPath1);
        var oldMtimeUtc2 = File.GetLastWriteTimeUtc(localPath2);
        return new VisualMergePreparation(localPath1, localPath2, oldMtimeUtc1, oldMtimeUtc2);
    }

    /// <summary>Ports run_merge().</summary>
    public Task RunMergeToolAsync(VisualMergePreparation preparation, CancellationToken cancellationToken) =>
        _fileClient.RunMergeToolAsync(preparation.LocalPath1, preparation.LocalPath2, cancellationToken);

    /// <summary>Propagates whichever side the merge tool actually modified —
    /// checking only the alpha-side copy (as the legacy visual_merge() did)
    /// silently no-ops when the user instead merges into the beta/right
    /// pane (e.g. WinMerge, alpha on the left): nothing gets detected,
    /// nothing gets pushed, and the same conflict just reappears with no
    /// explanation. Checking both sides fixes that. If both changed
    /// (shouldn't normally happen from a single merge-tool session), alpha
    /// wins the tie-break, deterministically. Once a winner is picked, its
    /// content is pushed to the *other* endpoint unconditionally, and back
    /// to the winning endpoint itself only if that side is SSH (its local
    /// copy was a downloaded temp file, not the real remote file — a local
    /// winning side was already edited in place, so no push-back needed
    /// there). Returns false if neither side changed, which the caller (the
    /// FR-9 UI loop) must treat as "re-present this same conflict" rather
    /// than moving on to the next one, matching resolve_single()'s retry
    /// loop.</summary>
    public async Task<bool> CompleteVisualMergeAsync(
        PendingConflict conflict, VisualMergePreparation preparation, DateTimeOffset timestampUtc, CancellationToken cancellationToken)
    {
        var alphaChanged = File.GetLastWriteTimeUtc(preparation.LocalPath1) != preparation.OldMtimeUtc1;
        var betaChanged = File.GetLastWriteTimeUtc(preparation.LocalPath2) != preparation.OldMtimeUtc2;
        if (!alphaChanged && !betaChanged)
        {
            return false;
        }

        var (winningPath, winningEndpoint, otherEndpoint) = alphaChanged
            ? (preparation.LocalPath1, conflict.Alpha, conflict.Beta)
            : (preparation.LocalPath2, conflict.Beta, conflict.Alpha);

        if (winningEndpoint.Transport == TransportKind.Ssh)
        {
            await _fileClient.PushLocalFileAsync(winningPath, winningEndpoint, conflict.FileName, cancellationToken);
        }
        await _fileClient.PushLocalFileAsync(winningPath, otherEndpoint, conflict.FileName, cancellationToken);

        _resolveLog.Append(
            conflict.SessionName, conflict.Alpha.Url, conflict.Beta.Url, conflict.FileName,
            "Visual merge", automatic: false, timestampUtc);
        return true;
    }
}

/// <summary>Local working copies fetched for the visual merge tool, plus
/// each side's modification time captured right before the tool runs
/// (compared afterwards to detect which side, if any, the user actually
/// changed).</summary>
public sealed record VisualMergePreparation(string LocalPath1, string LocalPath2, DateTime OldMtimeUtc1, DateTime OldMtimeUtc2);

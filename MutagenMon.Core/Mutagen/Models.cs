namespace MutagenMon.Core.Mutagen;

public enum TransportKind
{
    Local,
    Ssh,
}

public sealed record SessionEndpoint(string Url, TransportKind Transport, string? Server, string? RemoteDirectory);

/// <summary>One session's parsed `mutagen sync list` block. Null (in the
/// dictionaries below) means the session was not reported at all this poll —
/// distinct from an empty/unknown status string.</summary>
public sealed record ParsedSessionStatus(
    string Name,
    string? Id,
    string Status,
    bool IsDuplicate,
    bool HasProblems,
    bool HasConflicts,
    SessionEndpoint? Alpha,
    SessionEndpoint? Beta,
    StagingProgress? Staging = null);

/// <summary>Parsed from the two optional lines mutagen prints under
/// `Status: Staging files on &lt;side&gt;` while a transfer is actually in
/// flight (FR-2.2) — absent once staging is announced but not yet actively
/// moving bytes (observed on the poll right before it completes), so never
/// assume this is non-null just because <c>Status</c> starts with "Staging
/// files on".
/// <code>
/// Status: Staging files on beta
/// Staging progress: 0/1 - 34 MB - 0%
/// Current file: Tracetool.zip (34 MB/260 MB)
/// </code></summary>
public sealed record StagingProgress(
    int FilesCompleted,
    int FilesTotal,
    string BytesTransferred,
    int PercentComplete,
    string? CurrentFileName = null,
    string? CurrentFileBytesTransferred = null,
    string? CurrentFileTotalBytes = null);

public sealed record ConflictRecord(
    string AlphaName,
    string BetaName,
    string AlphaState,
    string BetaState,
    bool AutoResolved);

public sealed record MutagenSyncListResult(
    string RawLog,
    IReadOnlyDictionary<string, ParsedSessionStatus?> SessionStatuses,
    IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> Conflicts);

namespace MutagenMon.Core.Mutagen;

public enum TransportKind
{
    Local,
    Ssh,
}

/// <summary>The counts printed under an endpoint's "Synchronizable contents:"
/// line in `mutagen sync list` (sizes are ignored). While a session is halted
/// they are a frozen snapshot taken when the halt occurred (FR-58).</summary>
public sealed record EndpointContents(int Directories, int Files, int SymbolicLinks)
{
    /// <summary>FR-58.2: an endpoint is empty when only its root directory
    /// is left — no file, no symbolic link, at most one directory.</summary>
    public bool IsEmpty => Files == 0 && SymbolicLinks == 0 && Directories <= 1;
}

/// <param name="Contents">Null when the poll output carried no
/// "Synchronizable contents" block for this endpoint (e.g. not connected).</param>
public sealed record SessionEndpoint(
    string Url, TransportKind Transport, string? Server, string? RemoteDirectory, EndpointContents? Contents = null);

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

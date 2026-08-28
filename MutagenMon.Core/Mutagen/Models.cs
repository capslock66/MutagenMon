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
    SessionEndpoint? Beta);

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

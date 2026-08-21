using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

public enum ConflictResolutionChoice
{
    VisualMerge,
    AWins,
    BWins,
}

/// <summary>Size + last-modified of one side of a conflicting file, fetched
/// locally or via SSH `stat` depending on the endpoint's transport.</summary>
public sealed record FileStat(long SizeBytes, DateTimeOffset ModifiedUtc);

/// <summary>One conflict flattened out of a <see cref="MonitorSnapshot"/> for
/// the manual resolution batch (FR-9), carrying everything
/// <see cref="IConflictFileClient"/> needs without looking anything back up.</summary>
public sealed record PendingConflict(
    string SessionName,
    string FileName,
    SessionEndpoint Alpha,
    SessionEndpoint Beta);

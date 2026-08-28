using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

public enum ConflictResolutionChoice
{
    VisualMerge,
    AWins,
    BWins,
}

/// <summary>Size + last-modified of one side of a conflicting entry, fetched
/// locally or via SSH `stat` depending on the endpoint's transport.
/// <paramref name="IsDirectory"/> is true when the entry is a directory
/// rather than a regular file (SizeBytes is meaningless then, always 0);
/// <paramref name="Exists"/> is false when the entry is absent on this side
/// altogether (e.g. deleted) — ModifiedUtc is then <see cref="DateTimeOffset.MinValue"/>.</summary>
public sealed record FileStat(long SizeBytes, DateTimeOffset ModifiedUtc, bool IsDirectory = false, bool Exists = true);

/// <summary>One conflict flattened out of a <see cref="MonitorSnapshot"/> for
/// the manual resolution batch (FR-9), carrying everything
/// <see cref="IConflictFileClient"/> needs without looking anything back up.</summary>
public sealed record PendingConflict(
    string SessionName,
    string FileName,
    SessionEndpoint Alpha,
    SessionEndpoint Beta);

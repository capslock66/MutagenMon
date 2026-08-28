using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

/// <summary>
/// Implements the batch-assembly and
/// guard logic (FR-9.1/FR-9.3/FR-9.5) — pure, so it's testable without any
/// process/file IO. Excludes the actual per-conflict resolution actions,
/// which live in <see cref="ConflictResolutionService"/>.
///
/// Deliberate deviation from the legacy behavior: the legacy passes
/// <c>len(conflicts)</c> (the number of session keys) as the "total" shown
/// in "N of total", not the actual number of unresolved conflicts, which
/// undercounts whenever a session has more than one conflict. FR-9.1 asks
/// for "N of total [conflicts]", so <see cref="Flatten"/> counts real
/// unresolved conflicts instead.
/// </summary>
public static class ConflictBatchPlanner
{
    /// <summary>FR-9.5: refuse to start the batch workflow above this many
    /// pending (non-autoresolved) conflicts.</summary>
    public const int MaxBatchSize = 100;

    /// <summary>Flattens every non-autoresolved conflict across
    /// <paramref name="sessionNames"/> (in that order) into a resolvable list,
    /// skipping any conflict whose session isn't currently reporting both
    /// endpoints (nothing to compare/copy against).</summary>
    public static IReadOnlyList<PendingConflict> Flatten(
        IReadOnlyCollection<string> sessionNames,
        IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflictsBySession,
        IReadOnlyDictionary<string, ParsedSessionStatus?> sessionStatuses)
    {
        var result = new List<PendingConflict>();
        foreach (var sessionName in sessionNames)
        {
            if (!conflictsBySession.TryGetValue(sessionName, out var conflicts)) continue;
            sessionStatuses.TryGetValue(sessionName, out var status);
            if (status?.Alpha is null || status.Beta is null) continue;

            foreach (var conflict in conflicts)
            {
                if (conflict.AutoResolved) continue;
                result.Add(new PendingConflict(sessionName, conflict.AlphaName, status.Alpha, status.Beta));
            }
        }

        return result;
    }

    public static bool ExceedsBatchLimit(int pendingCount) => pendingCount > MaxBatchSize;

    /// <summary>Implements the default-selection rule (FR-9.3): prefer
    /// whichever side has the more recent modification time, defaulting to
    /// "B wins" on a tie.</summary>
    public static ConflictResolutionChoice DefaultChoice(FileStat alpha, FileStat beta) =>
        alpha.ModifiedUtc > beta.ModifiedUtc ? ConflictResolutionChoice.AWins : ConflictResolutionChoice.BWins;
}

using System.Text;
using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Status;

/// <summary>
/// Builds the status view's body (FR-8.1/FR-8.2) from the already-structured
/// <see cref="ParsedSessionStatus"/>/<see cref="ConflictRecord"/> data (rather than
/// regex-scrubbing the raw CLI log) so identifiers are simply
/// never included instead of stripped after the fact.
/// </summary>
public static class StatusReportFormatter
{
    /// <summary>One row per session (FR-8.1), in <paramref name="sessionNames"/>
    /// order, for the status view's grid.</summary>
    public static IReadOnlyList<SessionSummaryRow> BuildSessionRows(
        IReadOnlyCollection<string> sessionNames,
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses,
        IReadOnlyDictionary<string, DateTimeOffset?> lastChangedUtc)
    {
        var rows = new List<SessionSummaryRow>();
        foreach (var name in sessionNames)
        {
            statuses.TryGetValue(name, out var status);
            lastChangedUtc.TryGetValue(name, out var lastChanged);
            rows.Add(new SessionSummaryRow(
                name,
                BuildStatusDisplay(status),
                status?.Alpha?.Url ?? "(unknown)",
                status?.Beta?.Url ?? "(unknown)",
                lastChanged));
        }

        return rows;
    }

    /// <summary>While a large file is actively transferring, the bare
    /// "Staging files on &lt;side&gt;" status text never changes and gives no
    /// indication of progress (FR-2.2). When <see cref="StagingProgress"/> is
    /// available, show which file is currently uploading and how much data
    /// has moved instead. <see cref="StagingProgress.FilesCompleted"/> counts
    /// fully-finished files, so it is bumped by one here to report the file
    /// *in progress* (e.g. "0/2" while the first of two files is uploading
    /// becomes "1/2", not "0/2").</summary>
    private static string BuildStatusDisplay(ParsedSessionStatus? status)
    {
        if (status is null || string.IsNullOrEmpty(status.Status))
            return "(not running)";

        if (status.Staging is { CurrentFileName: { } fileName } staging)
        {
            var filesInProgress = Math.Min(staging.FilesCompleted + 1, staging.FilesTotal);
            return $"Uploading {filesInProgress}/{staging.FilesTotal} , {fileName} , {staging.BytesTransferred}";
        }

        return status.Status;
    }

    /// <summary>The "==== CONFLICTS ====" section, listing every conflict
    /// (autoresolving ones annotated) — empty string if there are none at all.</summary>
    public static string BuildConflictsSection(
        IReadOnlyCollection<string> sessionNames, IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts)
    {
        var sb = new StringBuilder();
        foreach (var name in sessionNames)
        {
            if (!conflicts.TryGetValue(name, out var list))
                continue;
            foreach (var conflict in list)
            {
                sb.Append(name).Append(": ").Append(conflict.AlphaName);
                if (conflict.AutoResolved)
                    sb.Append(" [autoresolving]");
                sb.Append('\n');
            }
        }

        if (sb.Length == 0)
            return "";
        return "==================== CONFLICTS ====================\n" + sb;
    }

    /// <summary>True if at least one conflict is
    /// NOT auto-resolved — this, not the mere presence of a conflict, decides
    /// whether the status view offers "Resolve conflicts" (FR-8.2) or is
    /// purely informational (FR-8.3).</summary>
    public static bool HasUnresolvedConflicts(IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts) =>
        conflicts.Values.Any(list => list.Any(c => !c.AutoResolved));
}

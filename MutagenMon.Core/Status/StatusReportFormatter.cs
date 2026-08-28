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
    /// <summary>One "Name:/Status:/Alpha:/Beta:" block per session, blank-line
    /// separated, in <paramref name="sessionNames"/> order.</summary>
    public static string BuildSessionsSection(
        IReadOnlyCollection<string> sessionNames, IReadOnlyDictionary<string, ParsedSessionStatus?> statuses)
    {
        var sb = new StringBuilder();
        foreach (var name in sessionNames)
        {
            if (sb.Length > 0) sb.Append('\n').Append('\n');

            statuses.TryGetValue(name, out var status);
            sb.Append("Name: ").Append(name).Append('\n');
            sb.Append("Status: ").Append(status is null || string.IsNullOrEmpty(status.Status) ? "(not running)" : status.Status).Append('\n');
            sb.Append("Alpha: ").Append(status?.Alpha?.Url ?? "(unknown)").Append('\n');
            sb.Append("Beta: ").Append(status?.Beta?.Url ?? "(unknown)");
        }

        return sb.ToString();
    }

    /// <summary>The "==== CONFLICTS ====" section, listing every conflict
    /// (autoresolving ones annotated) — empty string if there are none at all.</summary>
    public static string BuildConflictsSection(
        IReadOnlyCollection<string> sessionNames, IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts)
    {
        var sb = new StringBuilder();
        foreach (var name in sessionNames)
        {
            if (!conflicts.TryGetValue(name, out var list)) continue;
            foreach (var conflict in list)
            {
                sb.Append(name).Append(": ").Append(conflict.AlphaName);
                if (conflict.AutoResolved) sb.Append(" [autoresolving]");
                sb.Append('\n');
            }
        }

        if (sb.Length == 0) return "";
        return "==================== CONFLICTS ====================\n" + sb;
    }

    /// <summary>True if at least one conflict is
    /// NOT auto-resolved — this, not the mere presence of a conflict, decides
    /// whether the status view offers "Resolve conflicts" (FR-8.2) or is
    /// purely informational (FR-8.3).</summary>
    public static bool HasUnresolvedConflicts(IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts) =>
        conflicts.Values.Any(list => list.Any(c => !c.AutoResolved));
}

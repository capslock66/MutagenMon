using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Status;

namespace MutagenMon.Core.Notifications;

/// <summary>
/// Implements new-conflict notification tracking (FR-11.1) — tracks which
/// "session:file" conflict keys have already been notified
/// about and returns only the newly-appeared ones each poll. Auto-resolved
/// conflicts are excluded, per FR-10.2 ("...excluded from... the 'new
/// conflict' notification"). The seen-set is only replaced when there are
/// current (non-auto-resolved) conflicts, or once the worst code is back to
/// Ready, so a transient conflict-free poll mid-sync doesn't cause the same
/// conflict to be re-notified the next time it's reported.
/// </summary>
public sealed class ConflictNotificationTracker
{
    private HashSet<string> _seen = new();

    public IReadOnlyList<string> DetectNew(
        IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflictsBySession,
        SessionStatusCode worstCode)
    {
        var current = new HashSet<string>();
        foreach (var (sessionName, conflicts) in conflictsBySession)
            foreach (var conflict in conflicts)
            {
                if (conflict.AutoResolved)
                    continue;
                current.Add($"{sessionName}:{conflict.AlphaName}");
            }

        var newKeys = current.Where(key => !_seen.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();

        if (current.Count > 0 || worstCode == SessionStatusCode.Ready)
            _seen = current;

        return newKeys;
    }
}

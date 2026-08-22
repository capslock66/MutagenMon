namespace MutagenMon.Core.Notifications;

/// <summary>
/// Ports mutagenmonlib/wx/icon.py: TaskBarIcon.notify() (FR-11) — turns raw
/// signals (newly-seen conflicts, an auto-resolve event) into queued desktop
/// notifications, each independently gated by its own <c>NOTIFY_*</c> config
/// toggle (FR-11's "independently toggleable via configuration").
///
/// FR-11.3 (stuck-connection-restart notification) and FR-11.4
/// (profile-update notification) are intentionally not wired here: their
/// trigger points don't exist in this codebase yet — FR-11.3 depends on
/// FR-13's per-session restart-on-connecting-threshold logic (moved to
/// Phase 5), and FR-11.4 depends on FR-12's debounced profile-update signal
/// (only the raw, undebounced mtime signal exists so far, see
/// <see cref="ProfileWatch.SessionProfileWatcher"/>). See
/// requirements/05-wpf-migration-notes.md §6.
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly INotificationQueue _queue;
    private readonly bool _notifyConflicts;
    private readonly bool _notifyAutoresolve;

    public NotificationDispatcher(INotificationQueue queue, bool notifyConflicts, bool notifyAutoresolve)
    {
        _queue = queue;
        _notifyConflicts = notifyConflicts;
        _notifyAutoresolve = notifyAutoresolve;
    }

    /// <summary>FR-11.1: one notification grouping every newly-seen
    /// session:file conflict key. No-op if disabled or nothing is new.</summary>
    public void NotifyNewConflicts(IReadOnlyList<string> newConflictKeys)
    {
        if (!_notifyConflicts || newConflictKeys.Count == 0) return;
        _queue.Enqueue(new NotificationMessage("New conflicts", string.Join("\n", newConflictKeys)));
    }

    /// <summary>FR-11.2/FR-10.4: one notification per auto-resolved conflict,
    /// naming the rule applied and the file.</summary>
    public void NotifyAutoResolved(string sessionName, string fileName, string rule)
    {
        if (!_notifyAutoresolve) return;
        _queue.Enqueue(new NotificationMessage("Conflict auto-resolved", $"{sessionName}:{fileName} — {rule}"));
    }
}

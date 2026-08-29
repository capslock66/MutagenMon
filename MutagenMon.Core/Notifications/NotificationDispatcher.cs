namespace MutagenMon.Core.Notifications;

/// <summary>
/// Implements desktop notification dispatch (FR-11) — turns raw
/// signals (newly-seen conflicts, an auto-resolve event, a debounced profile
/// update) into queued desktop notifications, each independently gated by
/// its own <c>NOTIFY_*</c> config toggle (FR-11's "independently toggleable
/// via configuration").
///
/// FR-11.3's stuck-connection-restart notification and the always-on
/// duplicate-restart notification are wired via
/// <see cref="NotifyRestartedForConnecting"/>/<see cref="NotifyRestartedForDuplicate"/>,
/// called from FR-13's per-session restart logic in
/// <see cref="Monitoring.SessionMonitorService"/>.
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly INotificationQueue _queue;
    private readonly bool _notifyConflicts;
    private readonly bool _notifyAutoresolve;
    private readonly bool _notifyProfileUpdate;
    private readonly bool _notifyRestartConnection;

    public NotificationDispatcher(
        INotificationQueue queue, bool notifyConflicts, bool notifyAutoresolve, bool notifyProfileUpdate,
        bool notifyRestartConnection = false)
    {
        _queue = queue;
        _notifyConflicts = notifyConflicts;
        _notifyAutoresolve = notifyAutoresolve;
        _notifyProfileUpdate = notifyProfileUpdate;
        _notifyRestartConnection = notifyRestartConnection;
    }

    /// <summary>FR-11.1: one notification grouping every newly-seen
    /// session:file conflict key. No-op if disabled or nothing is new.</summary>
    public void NotifyNewConflicts(IReadOnlyList<string> newConflictKeys)
    {
        if (!_notifyConflicts || newConflictKeys.Count == 0)
            return;
        _queue.Enqueue(new NotificationMessage("New conflicts", string.Join("\n", newConflictKeys)));
    }

    /// <summary>FR-11.2/FR-10.4: one notification per auto-resolved conflict,
    /// naming the rule applied and the file.</summary>
    public void NotifyAutoResolved(string sessionName, string fileName, string rule)
    {
        if (!_notifyAutoresolve)
            return;
        _queue.Enqueue(new NotificationMessage("Conflict auto-resolved", $"{sessionName}:{fileName} — {rule}"));
    }

    /// <summary>FR-11.4/FR-12.3: one notification per session whose archive
    /// was just confirmed updated (debounced past MutagenProfileGraceSeconds, see
    /// <see cref="ProfileWatch.SessionProfileWatcher"/>). No-op if disabled
    /// or nothing was confirmed this poll.</summary>
    public void NotifyProfileUpdated(IReadOnlyList<string> confirmedUpdatedSessions)
    {
        if (!_notifyProfileUpdate)
            return;
        foreach (var sessionName in confirmedUpdatedSessions)
            _queue.Enqueue(new NotificationMessage("Updated", sessionName));
    }

    /// <summary>FR-13.2/FR-11.3b: a session restarted because it was detected
    /// as a duplicate name. Always raised, unconditionally — no config
    /// toggle gates this one.</summary>
    public void NotifyRestartedForDuplicate(string sessionName, string status)
    {
        _queue.Enqueue(new NotificationMessage(sessionName, $"Restarting duplicate: {status}"));
    }

    /// <summary>FR-13.3/FR-11.3: a session restarted because it stayed stuck
    /// "connecting" past <c>SessionMaxErrors</c>. Gated by
    /// <c>NotifyRestartConnection</c> (default disabled).</summary>
    public void NotifyRestartedForConnecting(string sessionName, string status)
    {
        if (!_notifyRestartConnection)
            return;
        _queue.Enqueue(new NotificationMessage(sessionName, $"Restarting connection: {status}"));
    }
}

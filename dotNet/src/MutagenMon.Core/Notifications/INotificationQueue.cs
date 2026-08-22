using System.Collections.Concurrent;

namespace MutagenMon.Core.Notifications;

/// <summary>Thread-safe handoff point between the background poller (where
/// FR-11 notification triggers are detected) and the UI thread that actually
/// shows them — same rationale as
/// <see cref="Monitoring.ISessionStateStore"/>, but a FIFO queue rather than
/// a latest-value snapshot, since each notification is a discrete event that
/// must be shown exactly once, not overwritable state.</summary>
public interface INotificationQueue
{
    void Enqueue(NotificationMessage message);

    /// <summary>Removes and returns every message queued since the last
    /// drain, in FIFO order. Intended to be called once per UI tick.</summary>
    IReadOnlyList<NotificationMessage> DrainAll();
}

public sealed class NotificationQueue : INotificationQueue
{
    private readonly ConcurrentQueue<NotificationMessage> _queue = new();

    public void Enqueue(NotificationMessage message) => _queue.Enqueue(message);

    public IReadOnlyList<NotificationMessage> DrainAll()
    {
        if (_queue.IsEmpty) return Array.Empty<NotificationMessage>();

        var drained = new List<NotificationMessage>();
        while (_queue.TryDequeue(out var message))
        {
            drained.Add(message);
        }
        return drained;
    }
}

namespace MutagenMon.Core.Monitoring;

/// <summary>Thread-safe handoff point between the background poller and the
/// tray icon's UI timer. A whole-snapshot reference swap gives atomic,
/// lock-free reads — simpler than the legacy's per-field
/// <c>threading.Lock</c>-guarded getters/setters.</summary>
public interface ISessionStateStore
{
    MonitorSnapshot Get();
    void Publish(MonitorSnapshot snapshot);
}

public sealed class SessionStateStore : ISessionStateStore
{
    private volatile MonitorSnapshot _snapshot;

    public SessionStateStore()
    {
        _snapshot = MonitorSnapshot.Initial(DateTimeOffset.UtcNow, enabled: true);
    }

    public MonitorSnapshot Get() => _snapshot;

    public void Publish(MonitorSnapshot snapshot) => _snapshot = snapshot;
}

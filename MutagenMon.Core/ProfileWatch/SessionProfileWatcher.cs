using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.ProfileWatch;

/// <summary>
/// Implements profile-directory change detection
/// (FR-12). Tracks two independent watermarks per session: a raw
/// mtime-increase signal (<see cref="Tick"/>'s return
/// value) feeding the tray icon's "just updated" flash (FR-12.3), and a
/// debounced/grace-gated signal (<see cref="ConfirmedUpdatedSessions"/>,
/// FR-12.2) feeding the FR-11.4 desktop notification.
/// </summary>
public sealed class SessionProfileWatcher
{
    private readonly IFileTimestampProvider _timestamps;
    private readonly string _profileDir;
    private readonly int _watchPeriodTicks;
    private readonly TimeSpan _grace;
    private readonly Dictionary<string, DateTimeOffset?> _lastSeenMtime = new();
    private readonly Dictionary<string, DateTimeOffset?> _lastGraceMtime = new();
    private List<string> _confirmedUpdates = new();
    private int _tick;

    public SessionProfileWatcher(
        IFileTimestampProvider timestamps, string profileDir, int watchPeriodTicks, int graceSeconds = 4)
    {
        _timestamps = timestamps;
        _profileDir = profileDir;
        _watchPeriodTicks = watchPeriodTicks;
        _grace = TimeSpan.FromSeconds(graceSeconds);
    }

    /// <summary>Session names whose archive mtime advanced by more than the
    /// grace period since the last confirmed update, as of the most recent
    /// <see cref="Tick"/> call (FR-12.2). Empty on any tick that confirms
    /// nothing new — including skipped ticks and baseline observations.</summary>
    public IReadOnlyList<string> ConfirmedUpdatedSessions => _confirmedUpdates;

    /// <summary>Call once per poll tick. Returns true if any watched session's
    /// archive mtime increased since the last check (0 or negative watch
    /// period disables the check entirely, matching the legacy's
    /// MUTAGEN_PROFILE_DIR_WATCH_PERIOD == 0 disable switch).</summary>
    public bool Tick(IReadOnlyDictionary<string, ParsedSessionStatus?> statuses)
    {
        _confirmedUpdates = new List<string>();
        if (_watchPeriodTicks <= 0)
            return false;
        _tick++;
        if (_tick % _watchPeriodTicks != 0)
            return false;

        var anyUpdating = false;
        foreach (var (name, status) in statuses)
        {
            if (status?.Id is null)
            {
                _lastSeenMtime[name] = null;
                _lastGraceMtime[name] = null;
                continue;
            }

            var archivePath = Path.Combine(_profileDir, "archives", status.Id);
            var mtime = _timestamps.GetLastWriteTimeUtc(archivePath);
            if (mtime is null)
            {
                // Reset both watermarks to ignore the first change in the future.
                _lastSeenMtime[name] = null;
                _lastGraceMtime[name] = null;
                continue;
            }

            if (_lastSeenMtime.TryGetValue(name, out var previous) && previous is { } prevValue && prevValue < mtime.Value)
                anyUpdating = true;
            _lastSeenMtime[name] = mtime;

            if (!_lastGraceMtime.TryGetValue(name, out var graceWatermark) || graceWatermark is null)
                _lastGraceMtime[name] = mtime;
            else if (graceWatermark.Value + _grace < mtime.Value)
            {
                _lastGraceMtime[name] = mtime;
                _confirmedUpdates.Add(name);
            }
        }
        return anyUpdating;
    }
}

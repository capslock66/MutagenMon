using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.ProfileWatch;

/// <summary>
/// Ports mutagenmonlib/wx/icon.py: TaskBarIcon.check_mutagen_profile_dir()
/// (FR-12) — but only the RAW mtime-increase signal, which is the only part
/// that feeds the tray icon's "just updated" flash (FR-12.3). The legacy
/// app's separate debounced/grace signal exists exclusively to gate desktop
/// notifications (FR-11.4), which are out of scope for this phase — so it is
/// intentionally not ported here to avoid building unused plumbing.
/// </summary>
public sealed class SessionProfileWatcher
{
    private readonly IFileTimestampProvider _timestamps;
    private readonly string _profileDir;
    private readonly int _watchPeriodTicks;
    private readonly Dictionary<string, DateTimeOffset?> _lastSeenMtime = new();
    private int _tick;

    public SessionProfileWatcher(IFileTimestampProvider timestamps, string profileDir, int watchPeriodTicks)
    {
        _timestamps = timestamps;
        _profileDir = profileDir;
        _watchPeriodTicks = watchPeriodTicks;
    }

    /// <summary>Call once per poll tick. Returns true if any watched session's
    /// archive mtime increased since the last check (0 or negative watch
    /// period disables the check entirely, matching the legacy's
    /// MUTAGEN_PROFILE_DIR_WATCH_PERIOD == 0 disable switch).</summary>
    public bool Tick(IReadOnlyDictionary<string, ParsedSessionStatus?> statuses)
    {
        if (_watchPeriodTicks <= 0) return false;
        _tick++;
        if (_tick % _watchPeriodTicks != 0) return false;

        var anyUpdating = false;
        foreach (var (name, status) in statuses)
        {
            if (status?.Id is null)
            {
                _lastSeenMtime[name] = null;
                continue;
            }

            var archivePath = Path.Combine(_profileDir, "archives", status.Id);
            var mtime = _timestamps.GetLastWriteTimeUtc(archivePath);
            if (mtime is null)
            {
                _lastSeenMtime[name] = null;
                continue;
            }

            if (_lastSeenMtime.TryGetValue(name, out var previous) && previous is { } prevValue && prevValue < mtime.Value)
            {
                anyUpdating = true;
            }
            _lastSeenMtime[name] = mtime;
        }
        return anyUpdating;
    }
}

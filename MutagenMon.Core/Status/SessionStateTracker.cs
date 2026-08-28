using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Status;

/// <summary>
/// Implements the per-session, cross-poll state machine (FR-3). Stateful by
/// design but entirely in-memory and deterministic, so it is easy to drive from tests
/// with a scripted sequence of <see cref="ParsedSessionStatus"/> inputs
/// (NFR-11).
/// </summary>
public sealed class SessionStateTracker
{
    /// <summary>Status prefixes considered "connecting" (FR-3/FR-13.3) — also
    /// consulted by <see cref="Monitoring.SessionMonitorService"/> to classify
    /// which of FR-13's three restart causes currently applies.</summary>
    public static readonly IReadOnlyList<string> ConnectingPrefixes = new[] { "Connecting to", "Waiting to connect", "Unknown" };
    private static readonly string[] WorkingPrefixes =
    {
        "Waiting 5 seconds for rescan", "Reconciling changes", "Staging files on", "Applying changes", "Saving archive",
    };
    private const string ScanningPrefix = "Scanning files";
    private const string ReadyPrefix = "Watching for changes";

    private readonly Dictionary<string, Entry> _entries = new();

    /// <summary>Feeds one session's latest poll result in and returns its
    /// (possibly unchanged) classification. Call once per known session on
    /// every poll.</summary>
    public SessionStatusCode Update(string sessionName, ParsedSessionStatus? parsed)
    {
        var entry = _entries.TryGetValue(sessionName, out var e) ? e : _entries[sessionName] = new Entry();

        if (parsed is null)
        {
            // legacy: `if not session_status[sname]:` branch — no session reported at all.
            TrackConsecutive(entry, statusKey: "", onSecondMiss: () => entry.Code = SessionStatusCode.NotRunning);
            entry.LastStatusKey = "";
            return entry.Code;
        }

        var status = parsed.Status;
        var statusKey = status + (parsed.IsDuplicate ? "dupl" : "");

        var isConnectingLike = string.IsNullOrEmpty(status)
            || parsed.IsDuplicate
            || ConnectingPrefixes.Any(p => status.StartsWith(p, StringComparison.Ordinal));

        if (isConnectingLike)
        {
            TrackConsecutive(entry, statusKey, onSecondMiss: () => entry.Code = SessionStatusCode.ConnectionError);
        }
        else if (status.StartsWith(ReadyPrefix, StringComparison.Ordinal))
        {
            entry.ConsecutiveMisses = 0;
            entry.Code = SessionStatusCode.Ready;
        }
        else if (WorkingPrefixes.Any(p => status.StartsWith(p, StringComparison.Ordinal)))
        {
            entry.ConsecutiveMisses = 0;
            entry.Code = SessionStatusCode.Syncing;
        }
        else if (status.StartsWith(ScanningPrefix, StringComparison.Ordinal))
        {
            entry.ConsecutiveMisses = 0;
            entry.Code = SessionStatusCode.Scanning;
        }
        // else: unrecognized status string — Code and ConsecutiveMisses are left
        // untouched, exactly like the legacy if/elif chain falling all the way
        // through with no matching branch.

        if (parsed.HasProblems) entry.Code = Min(entry.Code, SessionStatusCode.Problems);
        if (parsed.HasConflicts) entry.Code = Min(entry.Code, SessionStatusCode.Conflicts);

        entry.LastStatusKey = statusKey;
        return entry.Code;
    }

    /// <summary>The shared consecutive-abnormal-poll counter (FR-13's
    /// "consecutive-abnormal-poll counter" is this same counter, not a
    /// separate one — see FR-3 note in
    /// requirements/01-functional-requirements.md). Returns 0 for a session
    /// never observed yet.</summary>
    public int GetConsecutiveMisses(string sessionName) =>
        _entries.TryGetValue(sessionName, out var entry) ? entry.ConsecutiveMisses : 0;

    /// <summary>Resets the consecutive-miss counter immediately after a
    /// restart (FR-13.5), independent of the next poll's outcome.</summary>
    public void ResetConsecutiveMisses(string sessionName)
    {
        if (_entries.TryGetValue(sessionName, out var entry))
        {
            entry.ConsecutiveMisses = 0;
        }
    }

    private static void TrackConsecutive(Entry entry, string statusKey, Action onSecondMiss)
    {
        if (entry.LastStatusKey == statusKey)
        {
            entry.ConsecutiveMisses++;
            if (entry.ConsecutiveMisses > 1) onSecondMiss();
        }
        else
        {
            entry.ConsecutiveMisses = 0;
        }
    }

    private static SessionStatusCode Min(SessionStatusCode a, SessionStatusCode b) => (SessionStatusCode)Math.Min((int)a, (int)b);

    private sealed class Entry
    {
        public int ConsecutiveMisses;
        public string LastStatusKey = "";
        public SessionStatusCode Code = SessionStatusCode.Unknown;
    }
}

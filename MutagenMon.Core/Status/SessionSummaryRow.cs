namespace MutagenMon.Core.Status;

/// <summary>One row of the status view's session grid (FR-8.1). Combines
/// <c>mutagen sync list</c> data (Status/Alpha/Beta) with the archive-file
/// mtime tracked separately by <c>SessionProfileWatcher</c> (FR-12) — the
/// only reliable "something changed" signal, independent of whether a poll
/// ever caught the transfer in flight.</summary>
public sealed record SessionSummaryRow(
    string Name,
    string IconKey,
    string Status,
    int ConflictCount,
    string AlphaUrl,
    string BetaUrl,
    DateTimeOffset? LastChangedUtc,
    bool IsHalted = false,
    bool IsRootEmptyingHalt = false,
    bool IsPaused = false,
    string SyncMode = "")
{
    // IsHalted: the session reports a Mutagen safety halt (FR-57.3); the grid draws such a row in red.
    // IsRootEmptyingHalt: that halt is a root emptying, the only one with the "Accept deletion" action (FR-60.1).
    // IsPaused: the session is paused (FR-67), its row action offers "Resume" instead of "Pause".
    // SyncMode: the mode of the session's definition, as written on its create command (e.g. "two-way-safe"), shown in the Mode column (FR-8.1).

    /// <summary>The Conflict column: the number of unresolved
    /// (non-autoresolved) conflicts, blank when there are none.</summary>
    public string ConflictDisplay => ConflictCount > 0 ? ConflictCount.ToString() : "";

    public string LastChangedDisplay => LastChangedUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";
}

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
    string AlphaUrl,
    string BetaUrl,
    DateTimeOffset? LastChangedUtc)
{
    public string LastChangedDisplay => LastChangedUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";
}

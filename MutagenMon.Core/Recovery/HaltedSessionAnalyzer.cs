using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Status;

namespace MutagenMon.Core.Recovery;

public enum HaltKind
{
    /// <summary>The session does not report a Mutagen safety halt.</summary>
    None,

    /// <summary>The halt matches the configured root-emptying text (FR-57.5) — the only one MutagenMon resolves.</summary>
    RootEmptying,

    /// <summary>Any other `Halted…` status (root deletion, root type change): displayed and notified only.</summary>
    Other,
}

public enum EndpointSide
{
    Alpha,
    Beta,
}

/// <summary>What a poll tells about a halted session (FR-57.5, FR-58.2).
/// <see cref="Emptied"/> and <see cref="Purged"/> are both null when the
/// emptied side is undetermined: both sides empty, neither empty, or a
/// "Synchronizable contents" block missing or incomplete.</summary>
public sealed record HaltAnalysis(HaltKind Kind, EndpointSide? EmptiedSide, SessionEndpoint? Emptied, SessionEndpoint? Purged)
{
    public bool IsRootEmptying => Kind == HaltKind.RootEmptying;

    /// <summary>The number of files the purge of <see cref="Purged"/> is
    /// about to delete, as announced by the frozen snapshot (FR-58.3).</summary>
    public int? PurgedFileCount => Purged?.Contents?.Files;
}

/// <summary>FR-57.5 / FR-58.2: classifies a session's halt and identifies the
/// emptied side from the counts of its two endpoints. Pure: it only reads the
/// parsed poll result, never inspects the endpoints themselves (FR-58.3).</summary>
public static class HaltedSessionAnalyzer
{
    /// <param name="rootEmptyingText">The configured
    /// <c>HaltedOneSidedRootEmptying</c> text, compared case-insensitively
    /// against the start of the status; an empty text matches nothing.</param>
    public static HaltAnalysis Analyze(ParsedSessionStatus? status, string rootEmptyingText)
    {
        if (status is null || !SessionStateTracker.IsHalted(status.Status))
            return new HaltAnalysis(HaltKind.None, null, null, null);

        var text = rootEmptyingText.Trim();
        var kind = text.Length > 0 && status.Status.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            ? HaltKind.RootEmptying
            : HaltKind.Other;

        var alpha = status.Alpha;
        var beta = status.Beta;
        if (alpha?.Contents is not { } alphaContents || beta?.Contents is not { } betaContents
            || alphaContents.IsEmpty == betaContents.IsEmpty)
            return new HaltAnalysis(kind, null, null, null);

        return alphaContents.IsEmpty
            ? new HaltAnalysis(kind, EndpointSide.Alpha, alpha, beta)
            : new HaltAnalysis(kind, EndpointSide.Beta, beta, alpha);
    }
}

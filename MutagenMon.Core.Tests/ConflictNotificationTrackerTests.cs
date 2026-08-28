using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Notifications;
using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-11.1: which "session:file" conflict keys are newly-seen
/// each poll, matching the legacy app's conflict-diffing behavior.</summary>
public class ConflictNotificationTrackerTests
{
    private static ConflictRecord Conflict(string alphaName, bool autoResolved = false) =>
        new(alphaName, alphaName, "modified", "modified", autoResolved);

    private static IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> Conflicts(
        string sessionName, params ConflictRecord[] conflicts) =>
        new Dictionary<string, IReadOnlyList<ConflictRecord>> { [sessionName] = conflicts };

    [Fact]
    public void AFreshConflictIsReportedAsNew()
    {
        var tracker = new ConflictNotificationTracker();

        var newKeys = tracker.DetectNew(Conflicts("alpha-sync", Conflict("shared.txt")), SessionStatusCode.Conflicts);

        Assert.Equal(new[] { "alpha-sync:shared.txt" }, newKeys);
    }

    [Fact]
    public void ThePersistedConflictIsNotReportedAgainOnTheNextPoll()
    {
        var tracker = new ConflictNotificationTracker();
        tracker.DetectNew(Conflicts("alpha-sync", Conflict("shared.txt")), SessionStatusCode.Conflicts);

        var newKeys = tracker.DetectNew(Conflicts("alpha-sync", Conflict("shared.txt")), SessionStatusCode.Conflicts);

        Assert.Empty(newKeys);
    }

    [Fact]
    public void AnAutoResolvedConflictIsNeverReportedAsNew()
    {
        var tracker = new ConflictNotificationTracker();

        var newKeys = tracker.DetectNew(
            Conflicts("alpha-sync", Conflict("shared.txt", autoResolved: true)), SessionStatusCode.Conflicts);

        Assert.Empty(newKeys);
    }

    [Fact]
    public void AConflictThatReappearsAfterEverythingWentBackToReadyIsReportedAsNewAgain()
    {
        var tracker = new ConflictNotificationTracker();
        tracker.DetectNew(Conflicts("alpha-sync", Conflict("shared.txt")), SessionStatusCode.Conflicts);
        tracker.DetectNew(new Dictionary<string, IReadOnlyList<ConflictRecord>>(), SessionStatusCode.Ready);

        var newKeys = tracker.DetectNew(Conflicts("alpha-sync", Conflict("shared.txt")), SessionStatusCode.Conflicts);

        Assert.Equal(new[] { "alpha-sync:shared.txt" }, newKeys);
    }

    [Fact]
    public void ATransientConflictFreePollBelowReadyDoesNotResetTheSeenSet()
    {
        var tracker = new ConflictNotificationTracker();
        tracker.DetectNew(Conflicts("alpha-sync", Conflict("shared.txt")), SessionStatusCode.Conflicts);

        // Empty conflicts, but worst code isn't Ready yet (mid-sync) — legacy
        // had_conflicts is left untouched in this case.
        tracker.DetectNew(new Dictionary<string, IReadOnlyList<ConflictRecord>>(), SessionStatusCode.Syncing);

        var newKeys = tracker.DetectNew(Conflicts("alpha-sync", Conflict("shared.txt")), SessionStatusCode.Conflicts);

        Assert.Empty(newKeys);
    }

    [Fact]
    public void MultipleNewConflictsAreAllReportedGroupedAndSorted()
    {
        var tracker = new ConflictNotificationTracker();

        var newKeys = tracker.DetectNew(
            Conflicts("alpha-sync", Conflict("zeta.txt"), Conflict("alpha.txt")), SessionStatusCode.Conflicts);

        Assert.Equal(new[] { "alpha-sync:alpha.txt", "alpha-sync:zeta.txt" }, newKeys);
    }
}

using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SessionStateTrackerTests
{
    private static ParsedSessionStatus Status(string status, bool duplicate = false, bool problems = false, bool conflicts = false) =>
        new("s", "id", status, duplicate, problems, conflicts, Alpha: null, Beta: null);

    [Fact]
    public void ReadyStatusYieldsReadyCodeImmediately()
    {
        var tracker = new SessionStateTracker();
        Assert.Equal(SessionStatusCode.Ready, tracker.Update("s", Status("Watching for changes")));
    }

    [Fact]
    public void SyncingPrefixesAllMapToSyncingCode()
    {
        foreach (var prefix in new[] { "Waiting 5 seconds for rescan", "Reconciling changes", "Staging files on alpha", "Applying changes", "Saving archive" })
        {
            var tracker = new SessionStateTracker();
            Assert.Equal(SessionStatusCode.Syncing, tracker.Update("s", Status(prefix)));
        }
    }

    [Fact]
    public void ScanningPrefixMapsToScanningCode()
    {
        var tracker = new SessionStateTracker();
        Assert.Equal(SessionStatusCode.Scanning, tracker.Update("s", Status("Scanning files")));
    }

    [Fact]
    public void MissingSessionOnlyDowngradesToNotRunningAfterTwoConsecutiveMissesOfTheSameKey()
    {
        var tracker = new SessionStateTracker();
        tracker.Update("s", Status("Watching for changes")); // establishes Ready, key = "Watching for changes"

        // The first miss is a *key change* (real status -> empty), which resets the
        // consecutive-miss counter rather than incrementing it — matches the legacy
        // `if session_laststatus[sname] == estatus` comparison exactly. So the code
        // only actually downgrades on the *third* null poll in a row.
        Assert.Equal(SessionStatusCode.Ready, tracker.Update("s", null));
        Assert.Equal(SessionStatusCode.Ready, tracker.Update("s", null));
        Assert.Equal(SessionStatusCode.NotRunning, tracker.Update("s", null));
    }

    [Fact]
    public void MissingSessionOnAFreshTrackerDowngradesOnTheSecondPollNotTheThird()
    {
        // A brand new tracker's default key ("") already equals the missing-session
        // key ("") — unlike the Ready->missing transition above, there is no "free"
        // reset poll here, so this needs one fewer call than that scenario.
        var tracker = new SessionStateTracker();
        Assert.Equal(SessionStatusCode.Unknown, tracker.Update("s", null)); // counter=1, not enough yet
        Assert.Equal(SessionStatusCode.NotRunning, tracker.Update("s", null)); // counter=2, downgrades
    }

    [Fact]
    public void ConnectingStatusOnlyDowngradesToConnectionErrorOnTheSecondRepeatOfTheSameMessage()
    {
        var tracker = new SessionStateTracker();
        // 1st call: key change from the tracker's initial "" -> resets the counter.
        Assert.Equal(SessionStatusCode.Unknown, tracker.Update("s", Status("Connecting to beta")));
        // 2nd call: same key -> counter=1, still not enough.
        Assert.Equal(SessionStatusCode.Unknown, tracker.Update("s", Status("Connecting to beta")));
        // 3rd call: same key -> counter=2, now downgrades.
        Assert.Equal(SessionStatusCode.ConnectionError, tracker.Update("s", Status("Connecting to beta")));
    }

    [Fact]
    public void ConnectingCounterResetsWhenTheConnectingTextChanges()
    {
        var tracker = new SessionStateTracker();
        tracker.Update("s", Status("Connecting to beta"));
        // A different connecting-like message resets the consecutive counter (matches
        // legacy comparing the exact concatenated status+duplicate string each poll).
        Assert.Equal(SessionStatusCode.Unknown, tracker.Update("s", Status("Waiting to connect")));
        Assert.Equal(SessionStatusCode.Unknown, tracker.Update("s", Status("Waiting to connect")));
        Assert.Equal(SessionStatusCode.ConnectionError, tracker.Update("s", Status("Waiting to connect")));
    }

    [Fact]
    public void DuplicateFlagIsTreatedAsConnectingLikeRegardlessOfStatusText()
    {
        var tracker = new SessionStateTracker();
        tracker.Update("s", Status("Watching for changes", duplicate: true)); // key change, resets
        tracker.Update("s", Status("Watching for changes", duplicate: true)); // counter=1, not enough
        Assert.Equal(SessionStatusCode.ConnectionError, tracker.Update("s", Status("Watching for changes", duplicate: true))); // counter=2
    }

    [Fact]
    public void ProblemsFlagCapsCodeAt50EvenWhenReady()
    {
        var tracker = new SessionStateTracker();
        var code = tracker.Update("s", Status("Watching for changes", problems: true));
        Assert.Equal(SessionStatusCode.Problems, code);
    }

    [Fact]
    public void ConflictsFlagCapsCodeAt60EvenWhenReady()
    {
        var tracker = new SessionStateTracker();
        var code = tracker.Update("s", Status("Watching for changes", conflicts: true));
        Assert.Equal(SessionStatusCode.Conflicts, code);
    }

    [Fact]
    public void ProblemsFlagDoesNotUpgradeAWorseCode()
    {
        // min(30, 50) stays 30 — problems only ever caps downward, never raises.
        var tracker = new SessionStateTracker();
        var code = tracker.Update("s", Status("Scanning files", problems: true));
        Assert.Equal(SessionStatusCode.Scanning, code);
    }

    [Fact]
    public void UnrecognizedStatusTextLeavesPreviousCodeAndCounterUntouched()
    {
        // Faithful port of the legacy if/elif chain falling through with no match.
        var tracker = new SessionStateTracker();
        tracker.Update("s", Status("Watching for changes"));
        var code = tracker.Update("s", Status("Something mutagen never actually prints"));
        Assert.Equal(SessionStatusCode.Ready, code);
    }
}

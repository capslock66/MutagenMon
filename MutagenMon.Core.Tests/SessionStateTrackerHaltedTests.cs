using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-57.1: every Mutagen safety halt is classified as Problems.</summary>
public class SessionStateTrackerHaltedTests
{
    private static ParsedSessionStatus Status(string status, bool conflicts = false) =>
        new("s", "id", status, IsDuplicate: false, HasProblems: false, HasConflicts: conflicts, Alpha: null, Beta: null);

    [Theory]
    [InlineData("Halted due to one-sided root emptying")]
    [InlineData("Halted due to one-sided root deletion")]
    [InlineData("Halted due to root type change")]
    [InlineData("halted due to one-sided root emptying")]
    [InlineData("HALTED")]
    public void HaltedStatusIsProblemsOnTheVeryFirstPoll(string status)
    {
        var tracker = new SessionStateTracker();

        Assert.Equal(SessionStatusCode.Problems, tracker.Update("s", Status(status)));
    }

    [Fact]
    public void HaltedOverridesAReadyCode()
    {
        var tracker = new SessionStateTracker();
        Assert.Equal(SessionStatusCode.Ready, tracker.Update("s", Status("Watching for changes")));

        Assert.Equal(SessionStatusCode.Problems, tracker.Update("s", Status("Halted due to one-sided root emptying")));
    }

    [Fact]
    public void LeavingTheHaltRestoresTheNormalCode()
    {
        var tracker = new SessionStateTracker();
        tracker.Update("s", Status("Halted due to one-sided root emptying"));

        Assert.Equal(SessionStatusCode.Ready, tracker.Update("s", Status("Watching for changes")));
    }

    [Fact]
    public void ConflictsDoNotRaiseAHaltedSessionAboveProblems()
    {
        var tracker = new SessionStateTracker();

        Assert.Equal(SessionStatusCode.Problems, tracker.Update("s", Status("Halted due to one-sided root emptying", conflicts: true)));
    }

    [Fact]
    public void AHaltedSessionDoesNotAccumulateConnectionMisses()
    {
        var tracker = new SessionStateTracker();
        for (var i = 0; i < 5; i++)
            tracker.Update("s", Status("Halted due to one-sided root emptying"));

        Assert.Equal(0, tracker.GetConsecutiveMisses("s"));
    }

    [Theory]
    [InlineData("Halted due to one-sided root emptying", true)]
    [InlineData("halted", true)]
    [InlineData("Watching for changes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHaltedMatchesTheHaltedPrefixCaseInsensitively(string? status, bool expected) =>
        Assert.Equal(expected, SessionStateTracker.IsHalted(status));
}

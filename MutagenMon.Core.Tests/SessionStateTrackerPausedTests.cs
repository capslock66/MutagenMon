using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-67.1: detection of a paused session from its Mutagen status text.</summary>
public class SessionStateTrackerPausedTests
{
    [Theory]
    [InlineData("Paused")]
    [InlineData("paused")]
    [InlineData("[Paused]")]
    [InlineData("  Paused  ")]
    public void PausedStatusIsDetected(string status) => Assert.True(SessionStateTracker.IsPaused(status));

    [Theory]
    [InlineData("Watching for changes")]
    [InlineData("Halted due to root type change")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherStatusesAreNotPaused(string? status) => Assert.False(SessionStateTracker.IsPaused(status));
}

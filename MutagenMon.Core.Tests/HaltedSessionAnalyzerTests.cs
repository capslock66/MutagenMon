using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Recovery;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-57.5 / FR-58.2: halt kind and emptied-side identification.</summary>
public class HaltedSessionAnalyzerTests
{
    private const string RootEmptying = "Halted due to one-sided root emptying";

    private static ParsedSessionStatus Status(
        string status, EndpointContents? alpha, EndpointContents? beta, string alphaUrl = @"C:\sources\temp", string betaUrl = "robbie:sources/temp") =>
        new(
            "temp", "id", status, IsDuplicate: false, HasProblems: false, HasConflicts: false,
            Alpha: new SessionEndpoint(alphaUrl, TransportKind.Local, null, null, alpha),
            Beta: new SessionEndpoint(betaUrl, TransportKind.Ssh, "robbie", "sources/temp", beta));

    private static readonly EndpointContents Empty = new(1, 0, 0);
    private static readonly EndpointContents Full = new(1, 4, 0);

    [Fact]
    public void ANonHaltedSessionHasNoHalt()
    {
        var analysis = HaltedSessionAnalyzer.Analyze(Status("Watching for changes", Empty, Full), RootEmptying);

        Assert.Equal(HaltKind.None, analysis.Kind);
        Assert.Null(analysis.Emptied);
    }

    [Fact]
    public void AMissingSessionHasNoHalt() =>
        Assert.Equal(HaltKind.None, HaltedSessionAnalyzer.Analyze(null, RootEmptying).Kind);

    [Theory]
    [InlineData("Halted due to one-sided root emptying")]
    [InlineData("halted due to one-sided root emptying")]
    [InlineData("HALTED DUE TO ONE-SIDED ROOT EMPTYING (extra words after the text)")]
    public void TheConfiguredTextIsMatchedCaseInsensitivelyAtTheStartOfTheStatus(string status) =>
        Assert.True(HaltedSessionAnalyzer.Analyze(Status(status, Empty, Full), RootEmptying).IsRootEmptying);

    [Theory]
    [InlineData("Halted due to one-sided root deletion")]
    [InlineData("Halted due to root type change")]
    public void OtherHaltsAreNotRootEmptyings(string status)
    {
        var analysis = HaltedSessionAnalyzer.Analyze(Status(status, Empty, Full), RootEmptying);

        Assert.Equal(HaltKind.Other, analysis.Kind);
        Assert.False(analysis.IsRootEmptying);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyConfiguredTextMatchesNothing(string configuredText) =>
        Assert.Equal(HaltKind.Other, HaltedSessionAnalyzer.Analyze(Status(RootEmptying, Empty, Full), configuredText).Kind);

    [Fact]
    public void AnEmptiedAlphaMeansBetaIsPurged()
    {
        var analysis = HaltedSessionAnalyzer.Analyze(Status(RootEmptying, Empty, Full), RootEmptying);

        Assert.Equal(EndpointSide.Alpha, analysis.EmptiedSide);
        Assert.Equal(@"C:\sources\temp", analysis.Emptied!.Url);
        Assert.Equal("robbie:sources/temp", analysis.Purged!.Url);
        Assert.Equal(4, analysis.PurgedFileCount);
    }

    [Fact]
    public void AnEmptiedBetaMeansAlphaIsPurged()
    {
        var analysis = HaltedSessionAnalyzer.Analyze(Status(RootEmptying, Full, Empty), RootEmptying);

        Assert.Equal(EndpointSide.Beta, analysis.EmptiedSide);
        Assert.Equal("robbie:sources/temp", analysis.Emptied!.Url);
        Assert.Equal(@"C:\sources\temp", analysis.Purged!.Url);
    }

    [Fact]
    public void BothSidesEmptyIsUndetermined()
    {
        var analysis = HaltedSessionAnalyzer.Analyze(Status(RootEmptying, Empty, Empty), RootEmptying);

        Assert.True(analysis.IsRootEmptying);
        Assert.Null(analysis.EmptiedSide);
        Assert.Null(analysis.Purged);
    }

    [Fact]
    public void NeitherSideEmptyIsUndetermined()
    {
        var analysis = HaltedSessionAnalyzer.Analyze(Status(RootEmptying, Full, new EndpointContents(2, 1, 0)), RootEmptying);

        Assert.Null(analysis.EmptiedSide);
        Assert.Null(analysis.Emptied);
    }

    [Fact]
    public void AMissingContentsBlockIsUndetermined()
    {
        Assert.Null(HaltedSessionAnalyzer.Analyze(Status(RootEmptying, null, Full), RootEmptying).EmptiedSide);
        Assert.Null(HaltedSessionAnalyzer.Analyze(Status(RootEmptying, Empty, null), RootEmptying).EmptiedSide);
    }
}

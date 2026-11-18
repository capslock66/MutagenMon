using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-58.1/FR-58.2: reading the "Synchronizable contents" of each endpoint.</summary>
public class SessionMonitorServiceContentsParsingTests
{
    // The real output of `mutagen sync list temp` for a halted session whose alpha folder was emptied (2026-11-18).
    private const string HaltedAlphaEmptied = """
        Name: temp
        Identifier: sync_fLCXdLtWSvX0fLJ6ZEnISzInISlm3SNKm36lFBgutWb
        Alpha:
            URL: C:\sources\temp
            Connected: Yes
            Synchronizable contents:
                1 directory
                0 files (0 B)
                0 symbolic links
        Beta:
            URL: robbie:sources/temp
            Connected: Yes
            Synchronizable contents:
                1 directory
                4 files (0 B)
                0 symbolic links
        Status: Halted due to one-sided root emptying
        """;

    private static ParsedSessionStatus ParseSingle(string raw, string name = "temp")
    {
        var result = SessionMonitorService.Parse(raw, [name]);
        return result.SessionStatuses[name]!;
    }

    [Fact]
    public void ReadsTheContentsOfBothEndpointsOfAHaltedSession()
    {
        var status = ParseSingle(HaltedAlphaEmptied);

        Assert.Equal("Halted due to one-sided root emptying", status.Status);
        Assert.Equal(new EndpointContents(1, 0, 0), status.Alpha!.Contents);
        Assert.Equal(new EndpointContents(1, 4, 0), status.Beta!.Contents);
        Assert.True(status.Alpha.Contents!.IsEmpty);
        Assert.False(status.Beta.Contents!.IsEmpty);
    }

    [Fact]
    public void KeepsTheEndpointUrlsNextToTheirContents()
    {
        var status = ParseSingle(HaltedAlphaEmptied);

        Assert.Equal(@"C:\sources\temp", status.Alpha!.Url);
        Assert.Equal(TransportKind.Ssh, status.Beta!.Transport);
        Assert.Equal("sources/temp", status.Beta.RemoteDirectory);
    }

    [Fact]
    public void ToleratesPluralDirectoriesSingularFileAndASizeSuffix()
    {
        var status = ParseSingle("""
            Name: temp
            Alpha:
                URL: C:\a
                Synchronizable contents:
                    3 directories
                    1 file (12 B)
                    2 symbolic links
            Beta:
                URL: host:b
            Status: Watching for changes
            """);

        Assert.Equal(new EndpointContents(3, 1, 2), status.Alpha!.Contents);
    }

    [Fact]
    public void AnEndpointWithoutAContentsBlockHasNullContents()
    {
        var status = ParseSingle(HaltedAlphaEmptied.Replace("Synchronizable contents:", "Something else:"));

        Assert.Null(status.Alpha!.Contents);
        Assert.Null(status.Beta!.Contents);
    }

    [Fact]
    public void TheContentsBlockEndsAtTheFirstLineThatIsNotACount()
    {
        var status = ParseSingle("""
            Name: temp
            Alpha:
                URL: C:\a
                Synchronizable contents:
                    1 directory
                    0 files (0 B)
                    0 symbolic links
                Connected: Yes
                7 files are mentioned here but are not part of the block
            Beta:
                URL: host:b
            Status: Watching for changes
            """);

        Assert.Equal(new EndpointContents(1, 0, 0), status.Alpha!.Contents);
    }

    [Fact]
    public void AnIncompleteContentsBlockIsUndeterminedNotEmpty()
    {
        // The "symbolic links" line is missing: the missing count must not be read as 0 (FR-58.2).
        var status = ParseSingle("""
            Name: temp
            Alpha:
                URL: C:\a
                Synchronizable contents:
                    1 directory
                    0 files (0 B)
            Beta:
                URL: host:b
            Status: Watching for changes
            """);

        Assert.Null(status.Alpha!.Contents);
    }

    [Fact]
    public void AnEndpointWithAFileOrASymbolicLinkIsNotEmpty()
    {
        Assert.False(new EndpointContents(1, 1, 0).IsEmpty);
        Assert.False(new EndpointContents(1, 0, 1).IsEmpty);
        Assert.False(new EndpointContents(2, 0, 0).IsEmpty);
        Assert.True(new EndpointContents(1, 0, 0).IsEmpty);
        Assert.True(new EndpointContents(0, 0, 0).IsEmpty);
    }

    [Fact]
    public void ContentsOfOneSessionDoNotLeakIntoTheNextOne()
    {
        var result = SessionMonitorService.Parse(HaltedAlphaEmptied + "\n" + """
            Name: other
            Alpha:
                URL: C:\o
            Beta:
                URL: host:o
            Status: Watching for changes
            """, ["temp", "other"]);

        Assert.NotNull(result.SessionStatuses["temp"]!.Alpha!.Contents);
        Assert.Null(result.SessionStatuses["other"]!.Alpha!.Contents);
    }
}

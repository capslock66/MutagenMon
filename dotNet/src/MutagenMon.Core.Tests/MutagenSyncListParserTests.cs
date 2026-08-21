using MutagenMon.Core.Mutagen;
using Xunit;

namespace MutagenMon.Core.Tests;

public class MutagenSyncListParserTests
{
    private const string Raw = """
        Attempting to start Mutagen daemon...
        Started Mutagen daemon in background (terminate with "mutagen daemon stop")
        -----------------------------------------------------------------------------
        Name: photos-sync
        Identifier: sync_AAAA
        Labels:
        Status: Watching for changes
        Alpha:
        	URL: C:\Users\me\Photos
        Beta:
        	URL: myserver:/home/me/photos
        -----------------------------------------------------------------------------
        Name: docs-sync
        Identifier: sync_BBBB
        Status: Reconciling changes
        Alpha:
        	URL: C:\Users\me\Docs
        Beta:
        	URL: C:\Backup\Docs
        -----------------------------------------------------------------------------
        Name: shared-sync
        Identifier: sync_CCCC
        Status: Watching for changes
        Conflicts:
        Problems:
        Alpha:
        	URL: C:\Users\me\Shared
        Beta:
        	URL: myserver:/home/me/shared
        Conflicts:
        (alpha) report.docx (modified)
        (beta) report.docx (modified)
        (alpha) notes.txt (modified)
        (beta) notes.txt (modified)
        -----------------------------------------------------------------------------
        """;

    private static readonly string[] KnownSessions = { "photos-sync", "docs-sync", "shared-sync" };

    [Fact]
    public void StripsBannerNoiseAndDaemonStartupText()
    {
        var result = MutagenSyncListParser.Parse(Raw, KnownSessions);
        Assert.DoesNotContain("Attempting to start Mutagen daemon", result.RawLog);
        Assert.DoesNotContain("Started Mutagen daemon", result.RawLog);
        Assert.DoesNotContain("Labels:", result.RawLog);
    }

    [Fact]
    public void ParsesReadySessionWithLocalAndSshEndpoints()
    {
        var result = MutagenSyncListParser.Parse(Raw, KnownSessions);
        var status = result.SessionStatuses["photos-sync"];

        Assert.NotNull(status);
        Assert.Equal("sync_AAAA", status!.Id);
        Assert.Equal("Watching for changes", status.Status);
        Assert.False(status.IsDuplicate);
        Assert.False(status.HasConflicts);
        Assert.False(status.HasProblems);

        Assert.NotNull(status.Alpha);
        Assert.Equal(TransportKind.Local, status.Alpha!.Transport);
        Assert.Equal(@"C:\Users\me\Photos", status.Alpha.Url);

        Assert.NotNull(status.Beta);
        Assert.Equal(TransportKind.Ssh, status.Beta!.Transport);
        Assert.Equal("myserver", status.Beta.Server);
        Assert.Equal("/home/me/photos", status.Beta.RemoteDirectory);
    }

    [Fact]
    public void ParsesSshEndpointWithHomeRelativePathAsSshNotLocal()
    {
        const string raw = """
            Name: relative-sync
            Identifier: sync_DDDD
            Status: Watching for changes
            Alpha:
            	URL: C:\sources\appman
            Beta:
            	URL: tparent@pc-ub1:sources/appman
            """;

        var result = MutagenSyncListParser.Parse(raw, new[] { "relative-sync" });
        var status = result.SessionStatuses["relative-sync"];

        Assert.NotNull(status);
        Assert.Equal(TransportKind.Ssh, status!.Beta!.Transport);
        Assert.Equal("tparent@pc-ub1", status.Beta.Server);
        Assert.Equal("sources/appman", status.Beta.RemoteDirectory);
    }

    [Fact]
    public void ParsesSyncingSessionWithTwoLocalEndpoints()
    {
        var result = MutagenSyncListParser.Parse(Raw, KnownSessions);
        var status = result.SessionStatuses["docs-sync"];

        Assert.NotNull(status);
        Assert.Equal("Reconciling changes", status!.Status);
        Assert.Equal(TransportKind.Local, status.Alpha!.Transport);
        Assert.Equal(TransportKind.Local, status.Beta!.Transport);
    }

    [Fact]
    public void ParsesConflictsAndProblemsFlagsAndConflictRecords()
    {
        var result = MutagenSyncListParser.Parse(Raw, KnownSessions);
        var status = result.SessionStatuses["shared-sync"];

        Assert.NotNull(status);
        Assert.True(status!.HasConflicts);
        Assert.True(status.HasProblems);

        var conflicts = result.Conflicts["shared-sync"];
        Assert.Equal(2, conflicts.Count);
        Assert.Equal("report.docx", conflicts[0].AlphaName);
        Assert.Equal("report.docx", conflicts[0].BetaName);
        Assert.Equal("notes.txt", conflicts[1].AlphaName);
    }

    [Fact]
    public void MissingSessionYieldsNullStatusNotAnException()
    {
        var result = MutagenSyncListParser.Parse(Raw, new[] { "photos-sync", "never-created-sync" });
        Assert.Null(result.SessionStatuses["never-created-sync"]);
    }

    [Fact]
    public void UnknownSessionInOutputIsToleratedNotThrown()
    {
        // Robustness fix over the legacy behavior (see class doc comment on
        // MutagenSyncListParser): a stray session not in the known list must not
        // blow up parsing for every other session.
        var result = MutagenSyncListParser.Parse(Raw, new[] { "docs-sync" });
        Assert.NotNull(result.SessionStatuses["docs-sync"]);
        Assert.True(result.SessionStatuses.ContainsKey("photos-sync"));
        Assert.NotNull(result.SessionStatuses["photos-sync"]);
    }

    [Fact]
    public void DuplicateSessionNameIsFlaggedAndKeepsLastOccurrence()
    {
        const string dup = """
            Name: dup-sync
            Identifier: sync_FIRST
            Status: Scanning files
            Alpha:
            	URL: C:/A
            Beta:
            	URL: C:/B
            Name: dup-sync
            Identifier: sync_SECOND
            Status: Watching for changes
            Alpha:
            	URL: C:/A
            Beta:
            	URL: C:/B
            """;

        var result = MutagenSyncListParser.Parse(dup, new[] { "dup-sync" });
        var status = result.SessionStatuses["dup-sync"];

        Assert.NotNull(status);
        Assert.True(status!.IsDuplicate);
        Assert.Equal("sync_SECOND", status.Id);
        Assert.Equal("Watching for changes", status.Status);
    }

    [Theory]
    [InlineData("(x)", 2, 0)]
    [InlineData("(a(b)c)", 6, 0)]
    [InlineData("(a)(b)", 5, 3)]
    public void FindMatchingOpenParenMatchesFromTheEnd(string s, int closeIndex, int expectedOpenIndex)
    {
        var result = MutagenSyncListParser.FindMatchingOpenParen(s, closeIndex);
        Assert.Equal(expectedOpenIndex, result);
    }
}

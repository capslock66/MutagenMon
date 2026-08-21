using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

public class StatusReportFormatterTests
{
    private static readonly SessionEndpoint AlphaEndpoint = new("C:/local/photos", TransportKind.Local, null, null);
    private static readonly SessionEndpoint BetaEndpoint = new("myserver:/home/me/photos", TransportKind.Ssh, "myserver", "/home/me/photos");

    private static ParsedSessionStatus Watching(string name) =>
        new(name, "hidden-id", "Watching for changes", false, false, false, AlphaEndpoint, BetaEndpoint);

    [Fact]
    public void SessionsSectionListsEachSessionAndOmitsTheIdentifier()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = Watching("photos-sync") };

        var text = StatusReportFormatter.BuildSessionsSection(new[] { "photos-sync" }, statuses);

        Assert.Equal(
            "Name: photos-sync\n" +
            "Status: Watching for changes\n" +
            "Alpha: C:/local/photos\n" +
            "Beta: myserver:/home/me/photos",
            text);
        Assert.DoesNotContain("hidden-id", text);
    }

    [Fact]
    public void SessionsSectionShowsNotRunningForAMissingSession()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["docs-sync"] = null };

        var text = StatusReportFormatter.BuildSessionsSection(new[] { "docs-sync" }, statuses);

        Assert.Contains("Status: (not running)", text);
    }

    [Fact]
    public void ConflictsSectionIsEmptyWhenThereAreNoConflicts()
    {
        var conflicts = new Dictionary<string, IReadOnlyList<ConflictRecord>> { ["photos-sync"] = Array.Empty<ConflictRecord>() };

        var text = StatusReportFormatter.BuildConflictsSection(new[] { "photos-sync" }, conflicts);

        Assert.Equal("", text);
    }

    [Fact]
    public void ConflictsSectionAnnotatesAutoResolvingEntries()
    {
        var conflicts = new Dictionary<string, IReadOnlyList<ConflictRecord>>
        {
            ["photos-sync"] = new[]
            {
                new ConflictRecord("IMG_0231.jpg", "IMG_0231.jpg", "modified", "modified", AutoResolved: false),
                new ConflictRecord("notes.txt", "notes.txt", "modified", "modified", AutoResolved: true),
            },
        };

        var text = StatusReportFormatter.BuildConflictsSection(new[] { "photos-sync" }, conflicts);

        Assert.Equal(
            "==================== CONFLICTS ====================\n" +
            "photos-sync: IMG_0231.jpg\n" +
            "photos-sync: notes.txt [autoresolving]\n",
            text);
    }

    [Fact]
    public void HasUnresolvedConflictsIsFalseWhenEveryConflictIsAutoResolving()
    {
        var conflicts = new Dictionary<string, IReadOnlyList<ConflictRecord>>
        {
            ["photos-sync"] = new[] { new ConflictRecord("notes.txt", "notes.txt", "modified", "modified", AutoResolved: true) },
        };

        Assert.False(StatusReportFormatter.HasUnresolvedConflicts(conflicts));
    }

    [Fact]
    public void HasUnresolvedConflictsIsTrueWhenAtLeastOneConflictIsManual()
    {
        var conflicts = new Dictionary<string, IReadOnlyList<ConflictRecord>>
        {
            ["photos-sync"] = new[] { new ConflictRecord("IMG_0231.jpg", "IMG_0231.jpg", "modified", "modified", AutoResolved: false) },
        };

        Assert.True(StatusReportFormatter.HasUnresolvedConflicts(conflicts));
    }
}

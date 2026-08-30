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

    private static readonly IReadOnlyDictionary<string, DateTimeOffset?> NoLastChanged = new Dictionary<string, DateTimeOffset?>();
    private static readonly IReadOnlyDictionary<string, SessionStatusCode> NoCodes = new Dictionary<string, SessionStatusCode>();

    [Fact]
    public void SessionRowsListEachSessionAndOmitTheIdentifier()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = Watching("photos-sync") };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync" }, statuses, NoLastChanged, NoCodes, enabled: true);

        var row = Assert.Single(rows);
        Assert.Equal("photos-sync", row.Name);
        Assert.Equal("Watching for changes", row.Status);
        Assert.Equal("C:/local/photos", row.AlphaUrl);
        Assert.Equal("myserver:/home/me/photos", row.BetaUrl);
    }

    [Fact]
    public void SessionRowsShowUploadProgressWhenStagingAFile()
    {
        var staging = new StagingProgress(FilesCompleted: 0, FilesTotal: 2, BytesTransferred: "237 MB", PercentComplete: 50, CurrentFileName: "Tracetool.zip");
        var status = Watching("photos-sync") with { Status = "Staging files on beta", Staging = staging };
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = status };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync" }, statuses, NoLastChanged, NoCodes, enabled: true);

        Assert.Equal("Uploading 1/2 , Tracetool.zip , 237 MB", Assert.Single(rows).Status);
    }

    [Fact]
    public void SessionRowsUploadProgressCountsTheFileInProgressNotFilesCompleted()
    {
        var staging = new StagingProgress(FilesCompleted: 1, FilesTotal: 2, BytesTransferred: "260 MB", PercentComplete: 90, CurrentFileName: "Tracetool2.zip");
        var status = Watching("photos-sync") with { Status = "Staging files on beta", Staging = staging };
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = status };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync" }, statuses, NoLastChanged, NoCodes, enabled: true);

        Assert.Equal("Uploading 2/2 , Tracetool2.zip , 260 MB", Assert.Single(rows).Status);
    }

    [Fact]
    public void SessionRowsShowRawStatusWhenStagingHasNoCurrentFileYet()
    {
        var status = Watching("photos-sync") with { Status = "Staging files on beta", Staging = null };
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = status };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync" }, statuses, NoLastChanged, NoCodes, enabled: true);

        Assert.Equal("Staging files on beta", Assert.Single(rows).Status);
    }

    [Fact]
    public void SessionRowsIconKeyReflectsThatSessionsOwnCode()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?>
        {
            ["photos-sync"] = Watching("photos-sync"),
            ["docs-sync"] = Watching("docs-sync") with { Status = "Reconciling changes" },
        };
        var codes = new Dictionary<string, SessionStatusCode>
        {
            ["photos-sync"] = SessionStatusCode.Ready,
            ["docs-sync"] = SessionStatusCode.Syncing,
        };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync", "docs-sync" }, statuses, NoLastChanged, codes, enabled: true);

        Assert.Equal("green", rows[0].IconKey);
        Assert.Equal("green-sync", rows[1].IconKey);
    }

    [Fact]
    public void SessionRowsIconKeyReflectsDisabledMonitoring()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = Watching("photos-sync") };
        var codes = new Dictionary<string, SessionStatusCode> { ["photos-sync"] = SessionStatusCode.Ready };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync" }, statuses, NoLastChanged, codes, enabled: false);

        Assert.Equal("green-stop", Assert.Single(rows).IconKey);
    }

    [Fact]
    public void SessionRowsShowNotRunningForAMissingSession()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["docs-sync"] = null };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "docs-sync" }, statuses, NoLastChanged, NoCodes, enabled: true);

        Assert.Equal("(not running)", Assert.Single(rows).Status);
    }

    [Fact]
    public void SessionRowsIncludeLastChangedWhenAvailable()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = Watching("photos-sync") };
        var lastChanged = new Dictionary<string, DateTimeOffset?> { ["photos-sync"] = new DateTimeOffset(2026, 8, 29, 17, 25, 0, TimeSpan.Zero) };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync" }, statuses, lastChanged, NoCodes, enabled: true);

        Assert.Equal(lastChanged["photos-sync"], Assert.Single(rows).LastChangedUtc);
    }

    [Fact]
    public void SessionRowsLastChangedDisplayIsAPlaceholderWhenNull()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["photos-sync"] = Watching("photos-sync") };

        var rows = StatusReportFormatter.BuildSessionRows(new[] { "photos-sync" }, statuses, NoLastChanged, NoCodes, enabled: true);

        Assert.Equal("—", Assert.Single(rows).LastChangedDisplay);
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

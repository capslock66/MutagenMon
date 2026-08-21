using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using Xunit;

namespace MutagenMon.Core.Tests;

public class RestartReadinessTests
{
    private static ParsedSessionStatus Watching(string name) =>
        new(name, "id", "Watching for changes", false, false, false, null, null);

    [Fact]
    public void AllSessionsStoppedIsTrueWhenNoSessionReportsAStatus()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?>
        {
            ["alpha-sync"] = null,
            ["beta-sync"] = null,
        };

        Assert.True(RestartReadiness.AllSessionsStopped(statuses, statuses.Keys));
    }

    [Fact]
    public void AllSessionsStoppedIsFalseWhileAnySessionStillReportsAStatus()
    {
        var statuses = new Dictionary<string, ParsedSessionStatus?>
        {
            ["alpha-sync"] = null,
            ["beta-sync"] = Watching("beta-sync"),
        };

        Assert.False(RestartReadiness.AllSessionsStopped(statuses, statuses.Keys));
    }

    [Fact]
    public void AllSessionsStoppedIsTrueWhenASessionHasAnEmptyStatusString()
    {
        var terminated = new ParsedSessionStatus("alpha-sync", "id", "", false, false, false, null, null);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["alpha-sync"] = terminated };

        Assert.True(RestartReadiness.AllSessionsStopped(statuses, statuses.Keys));
    }
}

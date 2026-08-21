using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Resolution;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ConflictBatchPlannerTests
{
    private static readonly SessionEndpoint LocalAlpha = new("C:/local/alpha-sync", TransportKind.Local, null, null);
    private static readonly SessionEndpoint SshBeta = new("remote:/home/alpha-sync", TransportKind.Ssh, "remote", "/home/alpha-sync");

    private static ParsedSessionStatus Status(string name, bool withEndpoints = true) => new(
        name, "id", "Watching for changes", IsDuplicate: false, HasProblems: false, HasConflicts: true,
        withEndpoints ? LocalAlpha : null, withEndpoints ? SshBeta : null);

    [Fact]
    public void FlattenSkipsAutoResolvedConflicts()
    {
        var conflicts = new Dictionary<string, IReadOnlyList<ConflictRecord>>
        {
            ["alpha-sync"] = new[]
            {
                new ConflictRecord("a.txt", "a.txt", "modified", "modified", AutoResolved: true),
                new ConflictRecord("b.txt", "b.txt", "modified", "modified", AutoResolved: false),
            },
        };
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["alpha-sync"] = Status("alpha-sync") };

        var pending = ConflictBatchPlanner.Flatten(new[] { "alpha-sync" }, conflicts, statuses);

        Assert.Single(pending);
        Assert.Equal("b.txt", pending[0].FileName);
    }

    [Fact]
    public void FlattenSkipsSessionsMissingEitherEndpoint()
    {
        var conflicts = new Dictionary<string, IReadOnlyList<ConflictRecord>>
        {
            ["alpha-sync"] = new[] { new ConflictRecord("a.txt", "a.txt", "modified", "modified", AutoResolved: false) },
        };
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["alpha-sync"] = Status("alpha-sync", withEndpoints: false) };

        var pending = ConflictBatchPlanner.Flatten(new[] { "alpha-sync" }, conflicts, statuses);

        Assert.Empty(pending);
    }

    [Fact]
    public void FlattenCountsEveryConflictAcrossSessionsInOrder()
    {
        var conflicts = new Dictionary<string, IReadOnlyList<ConflictRecord>>
        {
            ["alpha-sync"] = new[]
            {
                new ConflictRecord("a.txt", "a.txt", "modified", "modified", AutoResolved: false),
                new ConflictRecord("b.txt", "b.txt", "modified", "modified", AutoResolved: false),
            },
            ["beta-sync"] = new[] { new ConflictRecord("c.txt", "c.txt", "modified", "modified", AutoResolved: false) },
        };
        var statuses = new Dictionary<string, ParsedSessionStatus?>
        {
            ["alpha-sync"] = Status("alpha-sync"),
            ["beta-sync"] = Status("beta-sync"),
        };

        var pending = ConflictBatchPlanner.Flatten(new[] { "alpha-sync", "beta-sync" }, conflicts, statuses);

        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, pending.Select(p => p.FileName));
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void ExceedsBatchLimitAtOneHundredOne(int count, bool expected)
    {
        Assert.Equal(expected, ConflictBatchPlanner.ExceedsBatchLimit(count));
    }

    [Fact]
    public void DefaultChoicePicksTheMoreRecentlyModifiedSide()
    {
        var older = new FileStat(100, DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = new FileStat(100, DateTimeOffset.UtcNow);

        Assert.Equal(ConflictResolutionChoice.AWins, ConflictBatchPlanner.DefaultChoice(newer, older));
        Assert.Equal(ConflictResolutionChoice.BWins, ConflictBatchPlanner.DefaultChoice(older, newer));
    }

    [Fact]
    public void DefaultChoicePicksBOnATie()
    {
        var same = DateTimeOffset.UtcNow;
        Assert.Equal(ConflictResolutionChoice.BWins, ConflictBatchPlanner.DefaultChoice(new FileStat(1, same), new FileStat(1, same)));
    }
}

using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Resolution;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>Auto-resolve engine test coverage (FR-10).</summary>
public class AutoResolveEngineTests
{
    private sealed class FakeConflictFileClient : IConflictFileClient
    {
        public readonly List<(SessionEndpoint Source, SessionEndpoint Destination, string RelativePath)> Copies = new();

        public Task<FileStat> StatAsync(SessionEndpoint endpoint, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new FileStat(1, DateTimeOffset.UtcNow));

        public Task CopyBetweenEndpointsAsync(SessionEndpoint source, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken)
        {
            Copies.Add((source, destination, relativePath));
            return Task.CompletedTask;
        }

        public Task<string> FetchLocalCopyAsync(SessionEndpoint endpoint, string relativePath, int side, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PushLocalFileAsync(string localPath, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RunMergeToolAsync(string localPath1, string localPath2, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static readonly SessionEndpoint LocalAlpha = new("C:/local/alpha-sync", TransportKind.Local, null, null);
    private static readonly SessionEndpoint SshBeta = new("remote:/home/alpha-sync", TransportKind.Ssh, "remote", "/home/alpha-sync");

    private static readonly IReadOnlyDictionary<string, ParsedSessionStatus?> SessionStatuses =
        new Dictionary<string, ParsedSessionStatus?>
        {
            ["alpha-sync"] = new ParsedSessionStatus(
                "alpha-sync", "id-a", "Watching for changes", IsDuplicate: false, HasProblems: false, HasConflicts: true, LocalAlpha, SshBeta),
        };

    private static (AutoResolveEngine Engine, FakeConflictFileClient Client, string LogDir) Build(
        IReadOnlyList<AutoResolveRule> rules, TimeSpan? historyAge = null)
    {
        var client = new FakeConflictFileClient();
        var logDir = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        var resolutionService = new ConflictResolutionService(client, new ResolveLogWriter(logDir));
        var engine = new AutoResolveEngine(rules, historyAge ?? TimeSpan.FromSeconds(30), resolutionService);
        return (engine, client, logDir);
    }

    private static Dictionary<string, IReadOnlyList<ConflictRecord>> OneConflict(bool autoResolved = false) =>
        new()
        {
            ["alpha-sync"] = new[] { new ConflictRecord("shared.txt", "shared.txt", "modified", "modified", autoResolved) },
        };

    private static AutoResolveRule Rule(string filePath, string resolve) => new() { FilePath = filePath, Resolve = resolve };

    [Fact]
    public async Task NoRulesLeavesConflictUnresolved()
    {
        var (engine, client, _) = Build(Array.Empty<AutoResolveRule>());

        var result = await engine.ApplyAsync(OneConflict(), SessionStatuses, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result["alpha-sync"][0].AutoResolved);
        Assert.Empty(client.Copies);
    }

    [Fact]
    public async Task NonMatchingRuleLeavesConflictUnresolved()
    {
        var (engine, client, _) = Build(new[] { Rule(@"\.idea", "A wins") });

        var result = await engine.ApplyAsync(OneConflict(), SessionStatuses, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result["alpha-sync"][0].AutoResolved);
        Assert.Empty(client.Copies);
    }

    [Fact]
    public async Task MatchingAWinsRuleCopiesAlphaOverBetaAndFlagsAutoResolved()
    {
        var (engine, client, logDir) = Build(new[] { Rule("shared", "A wins") });

        var result = await engine.ApplyAsync(OneConflict(), SessionStatuses, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(result["alpha-sync"][0].AutoResolved);
        Assert.Single(client.Copies);
        Assert.Equal(LocalAlpha, client.Copies[0].Source);
        Assert.Equal(SshBeta, client.Copies[0].Destination);
        var logText = File.ReadAllText(Path.Combine(logDir, "resolve.log"));
        Assert.Contains("[AUTO]", logText);
        Assert.Contains("A wins", logText);
    }

    [Fact]
    public async Task MatchingBWinsRuleCopiesBetaOverAlpha()
    {
        var (engine, client, _) = Build(new[] { Rule("shared", "B wins") });

        await engine.ApplyAsync(OneConflict(), SessionStatuses, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(client.Copies);
        Assert.Equal(SshBeta, client.Copies[0].Source);
        Assert.Equal(LocalAlpha, client.Copies[0].Destination);
    }

    [Fact]
    public async Task FirstMatchingRuleWinsWhenMultipleRulesMatch()
    {
        var (engine, client, _) = Build(new[] { Rule("shared", "A wins"), Rule("shared", "B wins") });

        await engine.ApplyAsync(OneConflict(), SessionStatuses, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(client.Copies);
        Assert.Equal(LocalAlpha, client.Copies[0].Source); // "A wins" rule listed first wins.
    }

    [Fact]
    public async Task SkipsConflictWhenSessionIsMissingAnEndpoint()
    {
        var (engine, client, _) = Build(new[] { Rule("shared", "A wins") });
        var statusesWithoutEndpoints = new Dictionary<string, ParsedSessionStatus?>
        {
            ["alpha-sync"] = new ParsedSessionStatus(
                "alpha-sync", "id-a", "Connecting...", false, false, false, Alpha: null, Beta: null),
        };

        var result = await engine.ApplyAsync(OneConflict(), statusesWithoutEndpoints, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result["alpha-sync"][0].AutoResolved);
        Assert.Empty(client.Copies);
    }

    [Fact]
    public async Task DoesNotReprocessAnAlreadyAutoResolvedConflictWithinTheGracePeriod()
    {
        var (engine, client, _) = Build(new[] { Rule("shared", "A wins") }, historyAge: TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.UtcNow;

        await engine.ApplyAsync(OneConflict(), SessionStatuses, t0, CancellationToken.None);
        var second = await engine.ApplyAsync(OneConflict(), SessionStatuses, t0.AddSeconds(10), CancellationToken.None);

        Assert.True(second["alpha-sync"][0].AutoResolved);
        Assert.Single(client.Copies); // still just the one copy from the first poll.
    }

    [Fact]
    public async Task ReprocessesOnceTheGracePeriodHasElapsed()
    {
        var (engine, client, _) = Build(new[] { Rule("shared", "A wins") }, historyAge: TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.UtcNow;

        await engine.ApplyAsync(OneConflict(), SessionStatuses, t0, CancellationToken.None);
        await engine.ApplyAsync(OneConflict(), SessionStatuses, t0.AddSeconds(31), CancellationToken.None);

        Assert.Equal(2, client.Copies.Count);
    }

    [Fact]
    public async Task RaisesConflictAutoResolvedEvent()
    {
        var (engine, _, _) = Build(new[] { Rule("shared", "A wins") });
        AutoResolvedEventArgs? raised = null;
        engine.ConflictAutoResolved += (_, e) => raised = e;

        await engine.ApplyAsync(OneConflict(), SessionStatuses, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Equal("alpha-sync", raised!.SessionName);
        Assert.Equal("shared.txt", raised.FileName);
        Assert.Equal("A wins", raised.Rule);
    }

    [Fact]
    public async Task LeavesAlreadyAutoResolvedConflictsFromAPreviousPollAloneWhenNoRulesConfigured()
    {
        var (engine, client, _) = Build(Array.Empty<AutoResolveRule>());

        var result = await engine.ApplyAsync(OneConflict(autoResolved: true), SessionStatuses, DateTimeOffset.UtcNow, CancellationToken.None);

        // The engine re-evaluates every conflict it's handed (the caller/parser
        // has no notion of "already resolved" across polls) — history, not the
        // incoming flag, is what prevents reprocessing (FR-10.3).
        Assert.False(result["alpha-sync"][0].AutoResolved);
        Assert.Empty(client.Copies);
    }
}

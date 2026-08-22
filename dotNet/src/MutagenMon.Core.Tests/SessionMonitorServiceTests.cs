using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.ProfileWatch;
using MutagenMon.Core.Resolution;
using MutagenMon.Core.Sessions;
using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>
/// Drives the whole Core pipeline (CLI client -> parser -> tracker ->
/// aggregator -> state store) through a fake <see cref="IMutagenCliClient"/> —
/// the concrete proof that none of this needs a real `mutagen` binary or a
/// real tray icon to verify (NFR-11), runnable on Linux.
/// </summary>
public class SessionMonitorServiceTests
{
    private sealed class FakeMutagenCliClient : IMutagenCliClient
    {
        private readonly Queue<Func<string>> _responses = new();
        public readonly List<string> TerminatedSessions = new();
        public string? FailTerminationFor;

        public void Enqueue(string response) => _responses.Enqueue(() => response);
        public void EnqueueFailure(string message) => _responses.Enqueue(() => throw new InvalidOperationException(message));

        public Task<string> GetSyncListRawAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()() : "");

        public Task TerminateSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            if (sessionName == FailTerminationFor)
            {
                throw new InvalidOperationException($"cannot terminate '{sessionName}'");
            }

            TerminatedSessions.Add(sessionName);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConflictFileClient : IConflictFileClient
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

    private static string ReadySession(string name, string id) => $"""
        Name: {name}
        Identifier: {id}
        Status: Watching for changes
        Alpha:
        	URL: C:/local/{name}
        Beta:
        	URL: remote:/home/{name}
        """;

    private static string ConflictedSession(string name, string id) => $"""
        Name: {name}
        Identifier: {id}
        Status: Watching for changes
        Conflicts:
        Alpha:
        	URL: C:/local/{name}
        Beta:
        	URL: remote:/home/{name}
        Conflicts:
        (alpha) shared.txt (modified)
        (beta) shared.txt (modified)
        """;

    private static SessionMonitorService BuildService(
        FakeMutagenCliClient cli, ISessionStateStore store, out IReadOnlyList<SessionDefinition> sessions)
    {
        sessions = new[]
        {
            new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ..."),
            new SessionDefinition("beta-sync", "mutagen sync create --name=beta-sync ..."),
        };
        return BuildService(cli, store, sessions);
    }

    private static SessionMonitorService BuildService(
        FakeMutagenCliClient cli, ISessionStateStore store, IReadOnlyList<SessionDefinition> sessions,
        IReadOnlyList<AutoResolveRule>? autoResolveRules = null, IConflictFileClient? conflictFileClient = null)
    {
        var options = Options.Create(new MutagenMonOptions
        {
            MutagenPollPeriodMs = 1000,
            StartEnabled = true,
            MutagenProfileDirWatchPeriod = 0, // disable profile watching for this test
            AutoResolve = autoResolveRules?.ToList() ?? new List<AutoResolveRule>(),
            AutoResolveHistoryAgeSeconds = 30,
        });
        var resolveLog = new ResolveLogWriter(
            Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N")));
        var conflictResolutionService = new ConflictResolutionService(
            conflictFileClient ?? new RecordingConflictFileClient(), resolveLog);
        return new SessionMonitorService(
            cli, store, options, sessions, new FileTimestampProvider(), conflictResolutionService,
            NullLogger<SessionMonitorService>.Instance);
    }

    [Fact]
    public async Task PublishesReadySnapshotWhenBothSessionsAreWatching()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        await service.PollOnceAsync(CancellationToken.None);

        var snapshot = store.Get();
        Assert.Equal(SessionStatusCode.Ready, snapshot.WorstCode);
        Assert.NotNull(snapshot.SessionStatuses["alpha-sync"]);
        Assert.NotNull(snapshot.SessionStatuses["beta-sync"]);
    }

    [Fact]
    public async Task WorstCodeReflectsTheSingleWorstSessionAcrossPolls()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        cli.Enqueue(ConflictedSession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(SessionStatusCode.Ready, store.Get().WorstCode);

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(SessionStatusCode.Conflicts, store.Get().WorstCode);
        Assert.Single(store.Get().Conflicts["alpha-sync"]);
    }

    [Fact]
    public async Task ACliFailureKeepsThePreviouslyPublishedSnapshotInstead()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        cli.EnqueueFailure("mutagen daemon is not responding");
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        await service.PollOnceAsync(CancellationToken.None);
        var afterSuccess = store.Get();

        await service.PollOnceAsync(CancellationToken.None);
        var afterFailure = store.Get();

        Assert.Same(afterSuccess, afterFailure); // no new snapshot published on failure
        Assert.Equal(SessionStatusCode.Ready, afterFailure.WorstCode);
    }

    [Fact]
    public async Task SessionThatNeverAppearsStaysUnknownAndDoesNotCrashTheWholePipeline()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ReadySession("alpha-sync", "id-a")); // beta-sync never shows up
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        await service.PollOnceAsync(CancellationToken.None);

        var snapshot = store.Get();
        // beta-sync has never been observed, so it's still at its initial Unknown
        // code (not yet downgraded to NotRunning — that needs a second consecutive
        // miss) — and Unknown(0) is worse than Ready(100), so it still drags down
        // the aggregate, same as the legacy get_worst_code() would.
        Assert.Equal(SessionStatusCode.Unknown, snapshot.WorstCode);
        Assert.Null(snapshot.SessionStatuses["beta-sync"]);
    }

    [Fact]
    public async Task NoConfiguredSessionsYieldsReady()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue("");
        var store = new SessionStateStore();
        var service = BuildService(cli, store, Array.Empty<SessionDefinition>());

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(SessionStatusCode.Ready, store.Get().WorstCode);
    }

    [Fact]
    public void IsEnabledReflectsTheConfiguredStartEnabledByDefault()
    {
        var cli = new FakeMutagenCliClient();
        var service = BuildService(cli, new SessionStateStore(), out _);

        Assert.True(service.IsEnabled);
    }

    [Fact]
    public async Task DisablingTerminatesEverySessionThatStillReportsAStatus()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        service.SetEnabled(false);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.False(store.Get().Enabled);
        Assert.Equal(new[] { "alpha-sync", "beta-sync" }, cli.TerminatedSessions);
    }

    [Fact]
    public async Task DisablingDoesNotTerminateASessionThatIsAlreadyNotReporting()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ReadySession("alpha-sync", "id-a")); // beta-sync never shows up
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        service.SetEnabled(false);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "alpha-sync" }, cli.TerminatedSessions);
    }

    [Fact]
    public async Task ATerminationFailureForOneSessionDoesNotPreventTheOthersFromBeingTerminated()
    {
        var cli = new FakeMutagenCliClient { FailTerminationFor = "alpha-sync" };
        cli.Enqueue(ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        service.SetEnabled(false);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "beta-sync" }, cli.TerminatedSessions);
    }

    [Fact]
    public async Task EnablingAgainStopsFurtherTerminationAttempts()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        cli.Enqueue(ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var service = BuildService(cli, store, out _);

        service.SetEnabled(false);
        await service.PollOnceAsync(CancellationToken.None);
        cli.TerminatedSessions.Clear();

        service.SetEnabled(true);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(cli.TerminatedSessions);
        Assert.True(store.Get().Enabled);
    }
}

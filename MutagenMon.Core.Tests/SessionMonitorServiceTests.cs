using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Notifications;
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
        public readonly List<string> CreatedSessions = new();
        public string? FailTerminationFor;
        public string? FailCreationFor;

        public void Enqueue(string response) => _responses.Enqueue(() => response);
        public void EnqueueFailure(string message) => _responses.Enqueue(() => throw new InvalidOperationException(message));

        public Task<string> GetSyncListRawAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()() : "");

        public Task TerminateSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            if (sessionName == FailTerminationFor)
                throw new InvalidOperationException($"cannot terminate '{sessionName}'");

            TerminatedSessions.Add(sessionName);
            return Task.CompletedTask;
        }

        public Task CreateSessionAsync(string rawCreateCommand, CancellationToken cancellationToken)
        {
            var name = rawCreateCommand;
            if (name == FailCreationFor)
                throw new InvalidOperationException($"cannot create '{rawCreateCommand}'");

            CreatedSessions.Add(rawCreateCommand);
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
        IReadOnlyList<AutoResolveRule>? autoResolveRules = null, IConflictFileClient? conflictFileClient = null,
        INotificationQueue? notificationQueue = null,
        bool notifyConflicts = true, bool notifyAutoresolve = true,
        bool startEnabled = true, bool notifyRestartConnection = false,
        int sessionMaxNoSession = 200, int sessionMaxDuplicate = 10000, int sessionMaxErrors = 30000,
        CapturingLogger<SessionMonitorService>? logger = null)
    {
        var options = Options.Create(new MutagenMonOptions
        {
            MutagenPollPeriodMs = 1000,
            StartEnabled = startEnabled,
            AutoResolve = autoResolveRules?.ToList() ?? new List<AutoResolveRule>(),
            AutoResolveHistoryAgeSeconds = 30,
            NotifyConflicts = notifyConflicts,
            NotifyAutoresolve = notifyAutoresolve,
            NotifyRestartConnection = notifyRestartConnection,
            SessionMaxNoSession = sessionMaxNoSession,
            SessionMaxDuplicate = sessionMaxDuplicate,
            SessionMaxErrors = sessionMaxErrors,
        });
        var conflictResolutionService = new ConflictResolutionService(
            conflictFileClient ?? new RecordingConflictFileClient(), NullLogger<ConflictResolutionService>.Instance);
        return new SessionMonitorService(
            cli, store, options, sessions, new FileTimestampProvider(), conflictResolutionService,
            notificationQueue ?? new NotificationQueue(),
            (ILogger<SessionMonitorService>?)logger ?? NullLogger<SessionMonitorService>.Instance);
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

    // FR-11.1 — new-conflict notifications.

    [Fact]
    public async Task ANewConflictQueuesOneGroupedNotification()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ConflictedSession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var queue = new NotificationQueue();
        var service = BuildService(cli, store, new[]
        {
            new SessionDefinition("alpha-sync", "..."), new SessionDefinition("beta-sync", "..."),
        }, notificationQueue: queue);

        await service.PollOnceAsync(CancellationToken.None);

        var message = Assert.Single(queue.DrainAll());
        Assert.Equal("New conflicts", message.Title);
        Assert.Equal("alpha-sync:shared.txt", message.Body);
    }

    [Fact]
    public async Task AConflictThatPersistsAcrossPollsIsNotNotifiedAgain()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ConflictedSession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        cli.Enqueue(ConflictedSession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var queue = new NotificationQueue();
        var sessions = new[] { new SessionDefinition("alpha-sync", "..."), new SessionDefinition("beta-sync", "...") };
        var service = BuildService(cli, store, sessions, notificationQueue: queue);

        await service.PollOnceAsync(CancellationToken.None);
        queue.DrainAll(); // consume the first notification, as the UI-thread timer would

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public async Task NoNewConflictNotificationIsQueuedWhenNotifyConflictsIsDisabled()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ConflictedSession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var queue = new NotificationQueue();
        var sessions = new[] { new SessionDefinition("alpha-sync", "..."), new SessionDefinition("beta-sync", "...") };
        var service = BuildService(cli, store, sessions, notificationQueue: queue, notifyConflicts: false);

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(queue.DrainAll());
    }

    // FR-11.2/FR-10.4 — auto-resolve notifications.

    [Fact]
    public async Task AnAutoResolvedConflictQueuesANotificationNamingTheRuleAndFile()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ConflictedSession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var queue = new NotificationQueue();
        var sessions = new[] { new SessionDefinition("alpha-sync", "..."), new SessionDefinition("beta-sync", "...") };
        var rules = new[] { new AutoResolveRule { FilePath = "shared", Resolve = "A wins" } };
        var service = BuildService(cli, store, sessions, autoResolveRules: rules, notificationQueue: queue);

        await service.PollOnceAsync(CancellationToken.None);

        var messages = queue.DrainAll();
        var autoResolveMessage = Assert.Single(messages, m => m.Title == "Conflict auto-resolved");
        Assert.Equal("alpha-sync:shared.txt — A wins", autoResolveMessage.Body);
    }

    [Fact]
    public async Task NoAutoResolveNotificationIsQueuedWhenNotifyAutoresolveIsDisabled()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(ConflictedSession("alpha-sync", "id-a") + "\n" + ReadySession("beta-sync", "id-b"));
        var store = new SessionStateStore();
        var queue = new NotificationQueue();
        var sessions = new[] { new SessionDefinition("alpha-sync", "..."), new SessionDefinition("beta-sync", "...") };
        var rules = new[] { new AutoResolveRule { FilePath = "shared", Resolve = "A wins" } };
        var service = BuildService(cli, store, sessions, autoResolveRules: rules, notificationQueue: queue, notifyAutoresolve: false);

        await service.PollOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(queue.DrainAll(), m => m.Title == "Conflict auto-resolved");
    }

    // FR-13 — automatic session recovery.

    [Fact]
    public async Task ANoSessionConditionRestartsOnceItExceedsSessionMaxNoSessionAndRaisesNoNotification()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue(""); // alpha-sync never reported at all
        cli.Enqueue("");
        var store = new SessionStateStore();
        var queue = new NotificationQueue();
        var sessions = new[] { new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ...") };
        var service = BuildService(cli, store, sessions, notificationQueue: queue, sessionMaxNoSession: 1);

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Empty(cli.TerminatedSessions);
        Assert.Empty(cli.CreatedSessions);

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Empty(cli.TerminatedSessions); // session already absent -> terminate is skipped
        Assert.Equal(new[] { "mutagen sync create --name=alpha-sync ..." }, cli.CreatedSessions);
        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public async Task ADuplicateSessionRestartsOnceItExceedsSessionMaxDuplicateAndAlwaysNotifiesEvenWhenConnectionNotificationIsDisabled()
    {
        var cli = new FakeMutagenCliClient();
        var duplicated = ReadySession("alpha-sync", "id-a") + "\n" + ReadySession("alpha-sync", "id-a2");
        cli.Enqueue(duplicated);
        cli.Enqueue(duplicated);
        cli.Enqueue(duplicated);
        var store = new SessionStateStore();
        var queue = new NotificationQueue();
        var sessions = new[] { new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ...") };
        var service = BuildService(
            cli, store, sessions, notificationQueue: queue, sessionMaxDuplicate: 1, notifyRestartConnection: false);

        // 1st poll: key change from the fresh tracker's default -> counter reset to 0.
        await service.PollOnceAsync(CancellationToken.None);
        Assert.Empty(cli.TerminatedSessions);
        // 2nd poll: same duplicate key -> counter=1, still not enough (threshold=1).
        await service.PollOnceAsync(CancellationToken.None);
        Assert.Empty(cli.TerminatedSessions);

        // 3rd poll: same duplicate key -> counter=2, exceeds threshold -> restart.
        await service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "alpha-sync" }, cli.TerminatedSessions);
        Assert.Equal(new[] { "mutagen sync create --name=alpha-sync ..." }, cli.CreatedSessions);
        var message = Assert.Single(queue.DrainAll());
        Assert.Equal("alpha-sync", message.Title);
    }

    [Fact]
    public async Task AStuckConnectingSessionRestartsOnceItExceedsSessionMaxErrorsAndOnlyNotifiesWhenEnabled()
    {
        var connecting = """
            Name: alpha-sync
            Identifier: id-a
            Status: Connecting to alpha
            """;
        var sessions = new[] { new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ...") };

        // Disabled: notification stays silent even though the restart still happens.
        // 3 polls needed: 1st resets the counter (key change from fresh), 2nd brings
        // it to 1 (not yet > threshold 1), 3rd brings it to 2 (exceeds it).
        var cliSilent = new FakeMutagenCliClient();
        cliSilent.Enqueue(connecting);
        cliSilent.Enqueue(connecting);
        cliSilent.Enqueue(connecting);
        var queueSilent = new NotificationQueue();
        var serviceSilent = BuildService(
            cliSilent, new SessionStateStore(), sessions, notificationQueue: queueSilent,
            sessionMaxErrors: 1, notifyRestartConnection: false);
        await serviceSilent.PollOnceAsync(CancellationToken.None);
        await serviceSilent.PollOnceAsync(CancellationToken.None);
        Assert.Empty(cliSilent.TerminatedSessions);
        await serviceSilent.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "alpha-sync" }, cliSilent.TerminatedSessions);
        Assert.Empty(queueSilent.DrainAll());

        // Enabled: same restart, but now a notification is raised.
        var cliNotify = new FakeMutagenCliClient();
        cliNotify.Enqueue(connecting);
        cliNotify.Enqueue(connecting);
        cliNotify.Enqueue(connecting);
        var queueNotify = new NotificationQueue();
        var serviceNotify = BuildService(
            cliNotify, new SessionStateStore(), sessions, notificationQueue: queueNotify,
            sessionMaxErrors: 1, notifyRestartConnection: true);
        await serviceNotify.PollOnceAsync(CancellationToken.None);
        await serviceNotify.PollOnceAsync(CancellationToken.None);
        await serviceNotify.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "alpha-sync" }, cliNotify.TerminatedSessions);
        Assert.Single(queueNotify.DrainAll());
    }

    [Fact]
    public async Task ATerminationFailureDuringRestartDoesNotPreventTheRecreateAttempt()
    {
        // Uses a "connecting" (session exists) scenario rather than the no-session
        // cause, since the latter now skips the terminate call entirely (the
        // session is already known to be absent) and would never exercise this path.
        var connecting = """
            Name: alpha-sync
            Identifier: id-a
            Status: Connecting to alpha
            """;
        var cli = new FakeMutagenCliClient { FailTerminationFor = "alpha-sync" };
        cli.Enqueue(connecting);
        cli.Enqueue(connecting);
        cli.Enqueue(connecting);
        var sessions = new[] { new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ...") };
        var service = BuildService(cli, new SessionStateStore(), sessions, sessionMaxErrors: 1);

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(cli.TerminatedSessions); // termination failed...
        Assert.Equal(new[] { "mutagen sync create --name=alpha-sync ..." }, cli.CreatedSessions); // ...but recreation still ran
    }

    [Fact]
    public async Task ACreationFailureDuringRestartStillResetsTheCounterSoItDoesNotRestartAgainNextPoll()
    {
        var cli = new FakeMutagenCliClient { FailCreationFor = "mutagen sync create --name=alpha-sync ..." };
        cli.Enqueue("");
        cli.Enqueue("");
        cli.Enqueue("");
        var sessions = new[] { new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ...") };
        var service = BuildService(cli, new SessionStateStore(), sessions, sessionMaxNoSession: 1);

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None); // restart attempt: terminate skipped (already absent), create throws (swallowed)
        Assert.Empty(cli.TerminatedSessions);
        Assert.Empty(cli.CreatedSessions); // creation failed before being recorded

        await service.PollOnceAsync(CancellationToken.None); // counter was reset after the restart attempt: no immediate re-restart

        Assert.Empty(cli.TerminatedSessions);
        Assert.Empty(cli.CreatedSessions);
    }

    [Fact]
    public async Task DisablingMonitoringPreventsAutomaticRestartsEvenPastTheThreshold()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue("");
        cli.Enqueue("");
        var sessions = new[] { new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ...") };
        var service = BuildService(cli, new SessionStateStore(), sessions, sessionMaxNoSession: 1, startEnabled: false);

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(cli.CreatedSessions);
    }

    [Fact]
    public async Task ARestartIsLoggedToTheMainLog()
    {
        var cli = new FakeMutagenCliClient();
        cli.Enqueue("");
        cli.Enqueue("");
        var sessions = new[] { new SessionDefinition("alpha-sync", "mutagen sync create --name=alpha-sync ...") };
        var logger = new CapturingLogger<SessionMonitorService>();
        var service = BuildService(
            cli, new SessionStateStore(), sessions, sessionMaxNoSession: 1, logger: logger);

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.Contains("Restarting: alpha-sync"));
    }
}

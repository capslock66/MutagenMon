using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Notifications;
using MutagenMon.Core.Recovery;
using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-61 (automatic resolution) and FR-62 (halt notifications).</summary>
public class HaltedAutoFixCoordinatorTests
{
    private const string RootEmptying = "Halted due to one-sided root emptying";
    private static readonly EndpointContents Empty = new(1, 0, 0);
    private static readonly EndpointContents Full = new(1, 4, 0);

    private sealed class FakeRecovery(MutagenMonOptions options, Func<int, RecoveryResult> next)
        : HaltedRecoveryService(
            new MutagenCliClient(Options.Create(options), NullLogger<MutagenCliClient>.Instance),
            new EndpointPurger(Options.Create(options), NullLogger<EndpointPurger>.Instance),
            Options.Create(options), NullLogger<HaltedRecoveryService>.Instance)
    {
        public int Calls;

        /// <summary>When set, a run waits for it before answering (to hold a run in flight).</summary>
        public TaskCompletionSource? Gate;

        public override async Task<RecoveryResult> RecoverAsync(string sessionName, ParsedSessionStatus? status, CancellationToken cancellationToken)
        {
            Calls++;
            if (Gate is not null)
                await Gate.Task;
            return next(Calls);
        }
    }

    private sealed class Fixture
    {
        public DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        public readonly NotificationQueue Queue = new();
        public readonly MutagenMonOptions Options;
        public readonly FakeRecovery Recovery;
        public readonly HaltedAutoFixCoordinator Coordinator;

        public Fixture(
            bool globalAutoFix = true, bool? sessionAutoFix = null, string createCommand = "mutagen sync create --name=temp A B",
            Func<int, RecoveryResult>? next = null, bool withRecovery = true, int cooldownSeconds = 60)
        {
            Options = new MutagenMonOptions
            {
                HaltedOneSidedRootEmptyingAutoFix = globalAutoFix,
                HaltedOneSidedRootEmptyingCooldownSeconds = cooldownSeconds,
            };
            Recovery = new FakeRecovery(Options, next ?? (_ => new RecoveryResult(true, "done", "ok")));
            Coordinator = new HaltedAutoFixCoordinator(
                Options, [new SessionDefinition("temp", createCommand, sessionAutoFix)], withRecovery ? Recovery : null,
                new NotificationDispatcher(Queue, true, true, true), NullLogger.Instance, () => Now);
        }

        public async Task PollAsync(ParsedSessionStatus? status, bool enabled = true)
        {
            Coordinator.Evaluate(new Dictionary<string, ParsedSessionStatus?> { ["temp"] = status }, enabled);
            await Coordinator.WhenIdleAsync();
        }

        public IReadOnlyList<NotificationMessage> Drain() => Queue.DrainAll();
    }

    private static ParsedSessionStatus Halted(
        string status = RootEmptying, EndpointContents? alpha = null, EndpointContents? beta = null, string betaUrl = "robbie:sources/temp") =>
        new(
            "temp", "id", status, false, false, false,
            new SessionEndpoint(@"C:\sources\temp", TransportKind.Local, null, null, alpha ?? Empty),
            new SessionEndpoint(betaUrl, TransportKind.Ssh, "robbie", "sources/temp", beta ?? Full));

    private static ParsedSessionStatus Healthy() =>
        new("temp", "id", "Watching for changes", false, false, false, null, null);

    // ---------------------------------------------------------------- the fix runs

    [Fact]
    public async Task AnEligibleHaltIsFixedWithoutAnyNotification()
    {
        var f = new Fixture();

        await f.PollAsync(Halted());

        Assert.Equal(1, f.Recovery.Calls);
        Assert.Empty(f.Drain());
    }

    [Fact]
    public async Task NothingNewStartsWhileTheCooldownRuns()
    {
        var f = new Fixture();
        await f.PollAsync(Halted());

        f.Now += TimeSpan.FromSeconds(30);
        await f.PollAsync(Halted());

        Assert.Equal(1, f.Recovery.Calls);
        Assert.Empty(f.Drain());
    }

    [Fact]
    public async Task TheCooldownSurvivesTheEndOfTheHaltEpisode()
    {
        var f = new Fixture();
        await f.PollAsync(Halted());
        await f.PollAsync(Healthy()); // the halt episode ends…

        f.Now += TimeSpan.FromSeconds(1);
        await f.PollAsync(Halted()); // …but a new halt right after must not start a purge + reset loop

        Assert.Equal(1, f.Recovery.Calls);

        f.Now += TimeSpan.FromSeconds(61);
        await f.PollAsync(Halted());

        Assert.Equal(2, f.Recovery.Calls);
        Assert.Empty(f.Drain());
    }

    [Fact]
    public async Task ARunThatFindsAManualRecoveryAlreadyRunningIsNeitherAFailureNorACooldown()
    {
        var f = new Fixture(next: calls => calls == 1
            ? new RecoveryResult(false, HaltedRecoveryService.BusyStep, "A recovery is already running for this session.")
            : new RecoveryResult(true, "done", "ok"));
        await f.PollAsync(Halted());
        Assert.Equal(1, f.Recovery.Calls);

        await f.PollAsync(Halted()); // no clock advance: the busy run left no cooldown behind

        Assert.Equal(2, f.Recovery.Calls);
        Assert.Empty(f.Drain()); // and no failure was counted either
    }

    [Fact]
    public async Task ARunThatReportsAfterTheEpisodeEndedCannotNotifyAboutIt()
    {
        var f = new Fixture(next: _ => new RecoveryResult(false, "purge", "boom"));
        // Two failures already: a third one WOULD abandon the fix and notify, unless it is ignored as belonging to a
        // halt that is over — which is exactly what is checked here.
        await f.PollAsync(Halted());
        f.Now += TimeSpan.FromSeconds(61);
        await f.PollAsync(Halted());
        Assert.Equal(2, f.Recovery.Calls);
        f.Now += TimeSpan.FromSeconds(61);

        var gate = new TaskCompletionSource();
        f.Recovery.Gate = gate;
        f.Coordinator.Evaluate(new Dictionary<string, ParsedSessionStatus?> { ["temp"] = Halted() }, monitoringEnabled: true); // third run starts
        f.Coordinator.Evaluate(new Dictionary<string, ParsedSessionStatus?> { ["temp"] = Healthy() }, monitoringEnabled: true); // episode over
        gate.SetResult();
        await f.Coordinator.WhenIdleAsync();

        Assert.Equal(3, f.Recovery.Calls);
        Assert.Empty(f.Drain());
    }

    [Fact]
    public async Task ANewEpisodeAfterThatStartsWithAFreshFailureCounter()
    {
        var f = new Fixture(next: _ => new RecoveryResult(false, "purge", "boom"));
        for (var i = 0; i < 2; i++)
        {
            await f.PollAsync(Halted());
            f.Now += TimeSpan.FromSeconds(61);
        }

        await f.PollAsync(Healthy()); // episode over with 2 failures counted
        for (var i = 0; i < 2; i++)
        {
            await f.PollAsync(Halted());
            f.Now += TimeSpan.FromSeconds(61);
        }

        Assert.Equal(4, f.Recovery.Calls);
        Assert.Empty(f.Drain()); // 2 + 2 failures never made 3 in a row inside one episode
    }

    [Fact]
    public async Task ANewSessionOverrideAppliesFromTheNextPollWithoutRestartingAnything()
    {
        var f = new Fixture(globalAutoFix: true);

        f.Coordinator.SetOverrides(new Dictionary<string, bool> { ["temp"] = false }); // the user chose "Disabled" in an Edit
        await f.PollAsync(Halted());

        Assert.Equal(0, f.Recovery.Calls);
        Assert.Single(f.Drain()); // and the halt is notified instead
    }

    [Fact]
    public async Task ASessionOverrideSetAfterConstructionCanTurnTheFixOn()
    {
        var f = new Fixture(globalAutoFix: false);

        f.Coordinator.SetOverrides(new Dictionary<string, bool> { ["temp"] = true });
        await f.PollAsync(Halted());

        Assert.Equal(1, f.Recovery.Calls);
        Assert.Empty(f.Drain());
    }

    [Fact]
    public async Task AnEmptiedBetaIsFixedToo()
    {
        var f = new Fixture();

        await f.PollAsync(Halted(alpha: Full, beta: Empty));

        Assert.Equal(1, f.Recovery.Calls);
    }

    // ---------------------------------------------------------------- the fix does not run (FR-61.1) -> halted notification (FR-62.1)

    [Fact]
    public async Task WithTheFlagOffTheHaltIsOnlyNotifiedOnce()
    {
        var f = new Fixture(globalAutoFix: false);

        await f.PollAsync(Halted());
        await f.PollAsync(Halted());
        await f.PollAsync(Halted());

        Assert.Equal(0, f.Recovery.Calls);
        var message = Assert.Single(f.Drain());
        Assert.Equal("Session halted", message.Title);
        Assert.Contains("temp", message.Body);
        Assert.Contains(RootEmptying, message.Body);
    }

    [Fact]
    public async Task ANewHaltEpisodeIsNotifiedAgain()
    {
        var f = new Fixture(globalAutoFix: false);
        await f.PollAsync(Halted());
        await f.PollAsync(Healthy());

        await f.PollAsync(Halted());

        Assert.Equal(2, f.Drain().Count);
    }

    [Fact]
    public async Task TheSessionDirectiveOverridesTheGlobalFlagInBothDirections()
    {
        var off = new Fixture(globalAutoFix: true, sessionAutoFix: false);
        await off.PollAsync(Halted());
        Assert.Equal(0, off.Recovery.Calls);
        Assert.Single(off.Drain());

        var on = new Fixture(globalAutoFix: false, sessionAutoFix: true);
        await on.PollAsync(Halted());
        Assert.Equal(1, on.Recovery.Calls);
        Assert.Empty(on.Drain());
    }

    [Fact]
    public async Task PausedMonitoringBlocksTheFixAndNotifiesTheHalt()
    {
        var f = new Fixture();

        await f.PollAsync(Halted(), enabled: false);

        Assert.Equal(0, f.Recovery.Calls);
        Assert.Single(f.Drain());
    }

    [Fact]
    public async Task AModeOtherThanTwoWaySafeBlocksTheFix()
    {
        var f = new Fixture(createCommand: "mutagen sync create --name=temp -m two-way-resolved A B");

        await f.PollAsync(Halted());

        Assert.Equal(0, f.Recovery.Calls);
        Assert.Single(f.Drain());
    }

    [Fact]
    public async Task AnUndeterminedEmptiedSideBlocksTheFix()
    {
        var f = new Fixture();

        await f.PollAsync(Halted(alpha: Empty, beta: Empty));

        Assert.Equal(0, f.Recovery.Calls);
        Assert.Single(f.Drain());
    }

    [Fact]
    public async Task AnUnsupportedSideToPurgeBlocksTheFix()
    {
        var f = new Fixture();

        await f.PollAsync(Halted(betaUrl: "docker://container/data"));

        Assert.Equal(0, f.Recovery.Calls);
        Assert.Single(f.Drain());
    }

    [Fact]
    public async Task WithoutARecoveryServiceTheHaltIsStillNotified()
    {
        var f = new Fixture(withRecovery: false);

        await f.PollAsync(Halted());

        Assert.Single(f.Drain());
    }

    [Theory]
    [InlineData("Halted due to one-sided root deletion")]
    [InlineData("Halted due to root type change")]
    public async Task OtherHaltsAreNeverFixedAndAreNotifiedOncePerEpisodeWhateverTheFlag(string status)
    {
        var f = new Fixture(globalAutoFix: true);

        await f.PollAsync(Halted(status));
        await f.PollAsync(Halted(status));

        Assert.Equal(0, f.Recovery.Calls);
        Assert.Single(f.Drain());
    }

    [Fact]
    public async Task ASessionThatIsNotHaltedNeverRaisesAnything()
    {
        var f = new Fixture();

        await f.PollAsync(Healthy());
        await f.PollAsync(null);

        Assert.Equal(0, f.Recovery.Calls);
        Assert.Empty(f.Drain());
    }

    // ---------------------------------------------------------------- failures (FR-61.4, FR-62.3)

    [Fact]
    public async Task ThreeConsecutiveFailedRunsAreAbandonedWithOneFailureNotification()
    {
        var f = new Fixture(next: _ => new RecoveryResult(false, "purge", "ssh exited with code 255"));

        for (var i = 0; i < 6; i++)
        {
            await f.PollAsync(Halted());
            f.Now += TimeSpan.FromSeconds(61);
        }

        Assert.Equal(3, f.Recovery.Calls);
        var message = Assert.Single(f.Drain());
        Assert.Equal("Automatic fix failed", message.Title);
        Assert.Contains("purge: ssh exited with code 255", message.Body);
    }

    [Fact]
    public async Task ARunThatCompletesButLeavesTheHaltCountsAsAFailureAfterTheCooldown()
    {
        var f = new Fixture(); // every run "succeeds", but the halt is reported again and again

        for (var i = 0; i < 8; i++)
        {
            await f.PollAsync(Halted());
            f.Now += TimeSpan.FromSeconds(61);
        }

        // Runs 1, 2 and 3 are started; the halt persisting after the third counts as the third failure.
        Assert.Equal(3, f.Recovery.Calls);
        var message = Assert.Single(f.Drain());
        Assert.Equal("Automatic fix failed", message.Title);
        Assert.Contains("still halted", message.Body);
    }

    [Fact]
    public async Task ACleanPollBetweenTwoRunsResetsTheFailureCounter()
    {
        var f = new Fixture(next: _ => new RecoveryResult(false, "purge", "boom"));
        await f.PollAsync(Halted());
        f.Now += TimeSpan.FromSeconds(61);
        await f.PollAsync(Halted());
        Assert.Equal(2, f.Recovery.Calls);

        await f.PollAsync(Healthy()); // the session recovered by itself: the episode is over

        for (var i = 0; i < 3; i++)
        {
            f.Now += TimeSpan.FromSeconds(61);
            await f.PollAsync(Halted());
        }

        Assert.Equal(5, f.Recovery.Calls); // 2 before, then 3 more: the counter had started again from zero
    }

    [Fact]
    public async Task TheCooldownAppliesAfterAFailedRunToo()
    {
        var f = new Fixture(next: _ => new RecoveryResult(false, "purge", "boom"));
        await f.PollAsync(Halted());

        f.Now += TimeSpan.FromSeconds(30);
        await f.PollAsync(Halted());

        Assert.Equal(1, f.Recovery.Calls);
    }

    [Fact]
    public async Task TheConfiguredCooldownIsUsed()
    {
        var f = new Fixture(cooldownSeconds: 10);
        await f.PollAsync(Halted());

        f.Now += TimeSpan.FromSeconds(9);
        await f.PollAsync(Halted());
        Assert.Equal(1, f.Recovery.Calls);

        f.Now += TimeSpan.FromSeconds(2);
        await f.PollAsync(Halted());
        Assert.Equal(2, f.Recovery.Calls);
    }

    [Fact]
    public async Task AnUnexpectedExceptionCountsAsAFailure()
    {
        // RecoverAsync is async now, so the exception comes back through the task, like a real failure would.
        var f = new Fixture(next: _ => throw new InvalidOperationException("unexpected"));

        await f.PollAsync(Halted());

        Assert.Equal(1, f.Recovery.Calls);
        Assert.Empty(f.Drain()); // one failure only: not abandoned yet
    }

    // ---------------------------------------------------------------- a manual recovery in progress (FR-59.2)

    [Fact]
    public async Task ARunningManualRecoveryIsWaitedForNotCountedAsAFailure()
    {
        var gate = new TaskCompletionSource();
        var options = new MutagenMonOptions { HaltedOneSidedRootEmptyingAutoFix = true };
        var purger = new GatedPurger(options, gate);
        var recovery = new HaltedRecoveryService(
            new NoopCli(options), purger, Options.Create(options), NullLogger<HaltedRecoveryService>.Instance);
        var queue = new NotificationQueue();
        var coordinator = new HaltedAutoFixCoordinator(
            options, [new SessionDefinition("temp", "mutagen sync create --name=temp A B")], recovery,
            new NotificationDispatcher(queue, true, true, true), NullLogger.Instance);

        var manual = recovery.RecoverAsync("temp", Halted(), CancellationToken.None); // blocked inside the purge
        coordinator.Evaluate(new Dictionary<string, ParsedSessionStatus?> { ["temp"] = Halted() }, monitoringEnabled: true);
        await coordinator.WhenIdleAsync();

        Assert.Equal(1, purger.Calls);
        Assert.Empty(queue.DrainAll());
        gate.SetResult();
        await manual;
    }

    private static (HaltedRecoveryService Recovery, CountingPurger Purger, HaltedAutoFixCoordinator Coordinator, NotificationQueue Queue)
        BuildWithRealService(bool globalAutoFix = true, TaskCompletionSource? gate = null)
    {
        var options = new MutagenMonOptions { HaltedOneSidedRootEmptyingAutoFix = globalAutoFix };
        var purger = new CountingPurger(options, gate);
        var recovery = new HaltedRecoveryService(new NoopCli(options), purger, Options.Create(options), NullLogger<HaltedRecoveryService>.Instance);
        var queue = new NotificationQueue();
        var coordinator = new HaltedAutoFixCoordinator(
            options, [new SessionDefinition("temp", "mutagen sync create --name=temp A B")], recovery,
            new NotificationDispatcher(queue, true, true, true), NullLogger.Instance);
        return (recovery, purger, coordinator, queue);
    }

    [Fact]
    public async Task AListingThatBeganBeforeTheLastRecoveryFinishedIsIgnored()
    {
        var (recovery, purger, coordinator, queue) = BuildWithRealService();
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["temp"] = Halted() };
        var beforeTheRecovery = DateTimeOffset.UtcNow.AddSeconds(-5);
        await recovery.RecoverAsync("temp", Halted(), CancellationToken.None); // a manual recovery finishes now

        // The listing began before it finished: its "halted" may be the very halt that recovery resolved.
        coordinator.Evaluate(statuses, monitoringEnabled: true, pollStartedUtc: beforeTheRecovery);
        await coordinator.WhenIdleAsync();
        Assert.Equal(1, purger.Calls);
        Assert.Empty(queue.DrainAll());

        coordinator.Evaluate(statuses, monitoringEnabled: true, pollStartedUtc: DateTimeOffset.UtcNow.AddSeconds(1));
        await coordinator.WhenIdleAsync();
        Assert.Equal(2, purger.Calls);
    }

    [Fact]
    public async Task NothingIsNotifiedWhileARecoveryRunsEvenIfMonitoringGetsPaused()
    {
        var gate = new TaskCompletionSource();
        var (recovery, _, coordinator, queue) = BuildWithRealService(gate: gate);
        var manual = recovery.RecoverAsync("temp", Halted(), CancellationToken.None); // blocked inside the purge

        coordinator.Evaluate(new Dictionary<string, ParsedSessionStatus?> { ["temp"] = Halted() }, monitoringEnabled: false);

        Assert.Empty(queue.DrainAll()); // FR-62.4
        gate.SetResult();
        await manual;
    }

    private sealed class CountingPurger(MutagenMonOptions options, TaskCompletionSource? gate = null)
        : EndpointPurger(Options.Create(options), NullLogger<EndpointPurger>.Instance)
    {
        public int Calls;

        public override async Task<PurgeResult> PurgeAsync(SessionEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls++;
            if (gate is not null)
                await gate.Task.WaitAsync(cancellationToken);
            return new PurgeResult(endpoint.Url);
        }
    }

    // A reset that does nothing: no real `mutagen` process in a unit test (NFR-11).
    private sealed class NoopCli(MutagenMonOptions options)
        : MutagenCliClient(Options.Create(options), NullLogger<MutagenCliClient>.Instance)
    {
        public override Task ResetSessionAsync(string sessionName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class GatedPurger(MutagenMonOptions options, TaskCompletionSource gate)
        : EndpointPurger(Options.Create(options), NullLogger<EndpointPurger>.Instance)
    {
        public int Calls;

        public override async Task<PurgeResult> PurgeAsync(SessionEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls++;
            await gate.Task;
            return new PurgeResult(endpoint.Url);
        }
    }
}

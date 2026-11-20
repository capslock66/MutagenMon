using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Recovery;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-59: purge the side that was not emptied, then reset — in that order, stopping at the first failure.</summary>
public class HaltedRecoveryServiceTests
{
    private const string RootEmptying = "Halted due to one-sided root emptying";

    private sealed class FakeCli(List<string> events)
        : MutagenCliClient(Options.Create(new MutagenMonOptions()), NullLogger<MutagenCliClient>.Instance)
    {
        public Exception? ResetFailure;

        public override Task ResetSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            events.Add($"reset:{sessionName}");
            return ResetFailure is null ? Task.CompletedTask : Task.FromException(ResetFailure);
        }
    }

    private sealed class FakePurger(List<string> events)
        : EndpointPurger(Options.Create(new MutagenMonOptions()), NullLogger<EndpointPurger>.Instance)
    {
        public Exception? Failure;
        public TaskCompletionSource? Gate;

        public override async Task<PurgeResult> PurgeAsync(SessionEndpoint endpoint, CancellationToken cancellationToken)
        {
            events.Add($"purge:{endpoint.Url}");
            if (Gate is not null)
                await Gate.Task.WaitAsync(cancellationToken);
            if (Failure is not null)
                throw Failure;
            return new PurgeResult(endpoint.Url);
        }
    }

    private static ParsedSessionStatus Halted(
        EndpointContents alpha, EndpointContents beta, string status = RootEmptying, string betaUrl = "robbie:sources/temp",
        TransportKind betaTransport = TransportKind.Ssh) =>
        new(
            "temp", "id", status, false, false, false,
            new SessionEndpoint(@"C:\sources\temp", TransportKind.Local, null, null, alpha),
            new SessionEndpoint(betaUrl, betaTransport, "robbie", "sources/temp", beta));

    private static readonly EndpointContents Empty = new(1, 0, 0);
    private static readonly EndpointContents Full = new(1, 4, 0);

    private static (HaltedRecoveryService Service, FakeCli Cli, FakePurger Purger, List<string> Events) Build()
    {
        var events = new List<string>();
        var cli = new FakeCli(events);
        var purger = new FakePurger(events);
        var service = new HaltedRecoveryService(cli, purger, Options.Create(new MutagenMonOptions()), NullLogger<HaltedRecoveryService>.Instance);
        return (service, cli, purger, events);
    }

    [Fact]
    public async Task ThePurgeOfTheOtherSideComesBeforeTheReset()
    {
        var (service, _, _, events) = Build();

        var result = await service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("done", result.Step);
        Assert.Equal(["purge:robbie:sources/temp", "reset:temp"], events);
    }

    [Fact]
    public async Task AnEmptiedBetaPurgesAlpha()
    {
        var (service, _, _, events) = Build();

        await service.RecoverAsync("temp", Halted(Full, Empty), CancellationToken.None);

        Assert.Equal([@"purge:C:\sources\temp", "reset:temp"], events);
    }

    [Fact]
    public async Task AFailedPurgeStopsTheSequenceBeforeTheReset()
    {
        var (service, _, purger, events) = Build();
        purger.Failure = new InvalidOperationException("ssh exited with code 255");

        var result = await service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("purge", result.Step);
        Assert.Contains("255", result.Message);
        Assert.Equal(["purge:robbie:sources/temp"], events);
    }

    [Fact]
    public async Task AFailedResetIsReportedAfterAnEffectivePurge()
    {
        var (service, cli, _, events) = Build();
        cli.ResetFailure = new InvalidOperationException("mutagen exited with code 1");

        var result = await service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("reset", result.Step);
        Assert.Equal(["purge:robbie:sources/temp", "reset:temp"], events);
    }

    [Fact]
    public async Task ANonRootEmptyingHaltIsRefusedBeforeAnyStep()
    {
        var (service, _, _, events) = Build();

        var result = await service.RecoverAsync("temp", Halted(Empty, Full, status: "Halted due to one-sided root deletion"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("check", result.Step);
        Assert.Empty(events);
    }

    [Fact]
    public async Task AnUndeterminedEmptiedSideIsRefusedBeforeAnyStep()
    {
        var (service, _, _, events) = Build();

        var result = await service.RecoverAsync("temp", Halted(Empty, Empty), CancellationToken.None);

        Assert.Equal("check", result.Step);
        Assert.Contains("cannot be determined", result.Message);
        Assert.Empty(events);
    }

    [Fact]
    public async Task AnUnsupportedSideToPurgeIsRefusedBeforeAnyStep()
    {
        var (service, _, _, events) = Build();

        var result = await service.RecoverAsync(
            "temp", Halted(Empty, Full, betaUrl: "docker://container/data", betaTransport: TransportKind.Ssh), CancellationToken.None);

        Assert.Equal("check", result.Step);
        Assert.Contains("not supported", result.Message);
        Assert.Empty(events);
    }

    [Fact]
    public async Task AMissingSessionIsRefused()
    {
        var (service, _, _, events) = Build();

        var result = await service.RecoverAsync("temp", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(events);
    }

    [Fact]
    public void CheckRecoverableReturnsNullForARecoverableHaltAndTheAnalysis()
    {
        var (service, _, _, _) = Build();

        var problem = service.CheckRecoverable(Halted(Empty, Full), out var analysis);

        Assert.Null(problem);
        Assert.Equal("robbie:sources/temp", analysis.Purged!.Url);
        Assert.Equal(4, analysis.PurgedFileCount);
    }

    [Fact]
    public void ARecheckAcceptsTheSameHaltBetweenTheSameSides()
    {
        var (service, _, _, _) = Build();
        service.CheckRecoverable(Halted(Empty, Full), out var shown);

        Assert.Null(service.CheckUnchanged("temp", shown, Halted(Empty, Full)));
    }

    [Fact]
    public void ARecheckRefusesASessionThatLeftTheHaltMeanwhile()
    {
        var (service, _, _, _) = Build();
        service.CheckRecoverable(Halted(Empty, Full), out var shown);

        var healthy = new ParsedSessionStatus("temp", "id", "Watching for changes", false, false, false, null, null);

        Assert.NotNull(service.CheckUnchanged("temp", shown, healthy));
        Assert.NotNull(service.CheckUnchanged("temp", shown, null));
    }

    [Fact]
    public void ARecheckRefusesAHaltWhoseSidesChanged()
    {
        var (service, _, _, _) = Build();
        service.CheckRecoverable(Halted(Empty, Full), out var shown); // alpha emptied, beta to purge

        var reason = service.CheckUnchanged("temp", shown, Halted(Full, Empty)); // now beta is the emptied one

        Assert.Contains("changed", reason);
    }

    [Fact]
    public async Task AListingThatBeganBeforeTheLastRecoveryFinishedIsRefused()
    {
        var (service, _, _, _) = Build();
        service.CheckRecoverable(Halted(Empty, Full), out var shown);
        var beforeTheRecovery = DateTimeOffset.UtcNow.AddSeconds(-5);
        await service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None); // whoever ran it

        Assert.True(service.ListingPredatesLastRecovery("temp", beforeTheRecovery));
        Assert.Contains("predates", service.CheckUnchanged("temp", shown, Halted(Empty, Full), beforeTheRecovery));

        var afterTheRecovery = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.False(service.ListingPredatesLastRecovery("temp", afterTheRecovery));
        Assert.Null(service.CheckUnchanged("temp", shown, Halted(Empty, Full), afterTheRecovery));
    }

    [Fact]
    public void ASessionNeverRecoveredHasNoListingToIgnore()
    {
        var (service, _, _, _) = Build();

        Assert.False(service.ListingPredatesLastRecovery("temp", DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task ABusyAnswerDoesNotCountAsACompletedRecovery()
    {
        var (service, _, purger, _) = Build();
        purger.Gate = new TaskCompletionSource();
        var first = service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);
        var startedWhileRunning = DateTimeOffset.UtcNow;

        await service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None); // busy: nothing ran

        Assert.False(service.ListingPredatesLastRecovery("temp", startedWhileRunning));
        purger.Gate.SetResult();
        await first;
    }

    [Fact]
    public async Task StoppingCancelsARunningRecoveryAndWaitsForIt()
    {
        var (service, _, purger, events) = Build();
        purger.Gate = new TaskCompletionSource();
        var run = service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);
        Assert.True(service.IsRunning("temp"));

        await service.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(service.IsRunning("temp"));
        Assert.Equal(["purge:robbie:sources/temp"], events); // no reset after the cancelled purge
    }

    [Fact]
    public async Task OnlyOneRecoveryRunsForASessionAtATime()
    {
        var (service, _, purger, events) = Build();
        purger.Gate = new TaskCompletionSource();

        var first = service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);
        Assert.True(service.IsRunning("temp"));

        var second = await service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(HaltedRecoveryService.BusyStep, second.Step);
        Assert.Contains("already running", second.Message);
        Assert.Equal(["purge:robbie:sources/temp"], events);

        purger.Gate.SetResult();
        Assert.True((await first).Success);
        Assert.False(service.IsRunning("temp"));
    }

    [Fact]
    public async Task TwoDifferentSessionsMayRecoverTogether()
    {
        var (service, _, purger, _) = Build();
        purger.Gate = new TaskCompletionSource();

        var first = service.RecoverAsync("one", Halted(Empty, Full), CancellationToken.None);
        var second = service.RecoverAsync("two", Halted(Empty, Full), CancellationToken.None);

        Assert.True(service.IsRunning("one"));
        Assert.True(service.IsRunning("two"));
        purger.Gate.SetResult();
        Assert.True((await first).Success);
        Assert.True((await second).Success);
    }

    [Fact]
    public async Task TheSessionIsFreeAgainAfterAFailure()
    {
        var (service, _, purger, _) = Build();
        purger.Failure = new InvalidOperationException("boom");
        await service.RecoverAsync("temp", Halted(Empty, Full), CancellationToken.None);

        Assert.False(service.IsRunning("temp"));
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Recovery;

/// <summary>Outcome of one recovery run. <see cref="Success"/> only means the
/// whole sequence ran (the purge and the reset both completed); whether the
/// session actually leaves the halt is seen on a later poll (FR-59 step 5).
/// <see cref="Step"/> is "check", "purge" or "reset" when it failed, "done" on
/// success.</summary>
public sealed record RecoveryResult(bool Success, string Step, string Message);

/// <summary>
/// The recovery sequence of FR-59, shared by the manual action (FR-60) and the
/// automatic fix (FR-61): purge the side that was not emptied, then run
/// `mutagen sync reset` immediately — the purge must come first because the
/// reset restarts the synchronisation at once. No `resume`, no waiting, no
/// inspection of the emptied side's current content (FR-59 step 4).
///
/// The recovery makes the emptied side authoritative: <b>the content of the
/// purged side is destroyed</b>, including files Mutagen ignores and files not
/// yet synchronised (FR-59.4).
/// </summary>
public class HaltedRecoveryService
{
    private readonly MutagenCliClient _cliClient;
    private readonly EndpointPurger _purger;
    private readonly string _rootEmptyingText;
    private readonly ILogger<HaltedRecoveryService> _logger;
    private readonly HashSet<string> _running = new();
    private readonly List<Task> _active = new();
    private readonly Dictionary<string, DateTimeOffset> _lastCompletedUtc = new();
    private readonly CancellationTokenSource _shutdown = new();

    public HaltedRecoveryService(
        MutagenCliClient cliClient, EndpointPurger purger, IOptions<MutagenMonOptions> options, ILogger<HaltedRecoveryService> logger)
    {
        _cliClient = cliClient;
        _purger = purger;
        _rootEmptyingText = options.Value.HaltedOneSidedRootEmptying;
        _logger = logger;
    }

    /// <summary>Null when the session can be recovered; otherwise the reason it
    /// cannot (not a root-emptying halt, emptied side undetermined, side to
    /// purge not supported — FR-58.2, FR-59.1). <paramref name="analysis"/> is
    /// always set.</summary>
    public string? CheckRecoverable(ParsedSessionStatus? status, out HaltAnalysis analysis)
    {
        analysis = HaltedSessionAnalyzer.Analyze(status, _rootEmptyingText);
        if (!analysis.IsRootEmptying)
            return "the session is not halted by a root emptying";
        if (analysis.Emptied is null || analysis.Purged is null)
            return "the emptied side cannot be determined from the 'Synchronizable contents' of the two sides";
        if (!EndpointPurger.IsSupported(analysis.Purged, out var reason))
            return $"the side to purge is not supported: {reason}";
        return null;
    }

    /// <summary>The <see cref="RecoveryResult.Step"/> of a run that did nothing
    /// because another recovery of the same session was already running
    /// (FR-59.2): neither a success nor a failure.</summary>
    public const string BusyStep = "busy";

    /// <summary>FR-60.3: the confirmation dialog of the manual action can stay
    /// open for a long time, and the session may have left the halt, or halted
    /// again for another reason, meanwhile. Null when <paramref name="fresh"/>
    /// (the latest poll) still shows a recoverable root-emptying halt with the
    /// same two sides as <paramref name="shown"/> (the ones the user
    /// confirmed); otherwise the reason it must be refused. Destroying the
    /// content of a side of a session that synchronises again would be
    /// propagated as ordinary deletions.</summary>
    /// <param name="sessionName">With <paramref name="pollStartedUtc"/>: the listing must not
    /// predate the end of the last recovery of this session, whoever ran it.</param>
    public string? CheckUnchanged(
        string sessionName, HaltAnalysis shown, ParsedSessionStatus? fresh, DateTimeOffset? pollStartedUtc = null)
    {
        if (pollStartedUtc is { } pollStarted && ListingPredatesLastRecovery(sessionName, pollStarted))
            return "the status predates the last recovery of this session";

        var problem = CheckRecoverable(fresh, out var current);
        if (problem is not null)
            return problem;

        if (current.Emptied!.Url != shown.Emptied?.Url || current.Purged!.Url != shown.Purged?.Url)
            return "the halt changed while the confirmation was open";
        return null;
    }

    /// <summary>True when a `mutagen sync list` that began at <paramref name="pollStartedUtc"/>
    /// may have been taken before the last recovery of this session finished:
    /// its `Halted` could be the one that recovery had just resolved, and
    /// acting on it would purge a session that synchronises again.</summary>
    public bool ListingPredatesLastRecovery(string sessionName, DateTimeOffset pollStartedUtc)
    {
        lock (_running)
            return _lastCompletedUtc.TryGetValue(sessionName, out var completed) && pollStartedUtc < completed;
    }

    /// <summary>Cancels every recovery that is running (a reload of the
    /// configuration) and completes once they have all stopped, so none of
    /// them can overlap the services that replace this one (FR-59.2).</summary>
    public async Task StopAsync()
    {
        await _shutdown.CancelAsync();
        Task[] active;
        lock (_running)
            active = _active.ToArray();
        await Task.WhenAll(active);
    }

    /// <summary>FR-59.2: true while a recovery of this session is running.</summary>
    public bool IsRunning(string sessionName)
    {
        lock (_running)
            return _running.Contains(sessionName);
    }

    /// <summary>Runs the sequence for <paramref name="sessionName"/> from the
    /// snapshot <paramref name="status"/> of the poll that reported the halt
    /// (FR-58.3). Never throws for a failing step: the failure is returned.
    /// Only one recovery runs for a given session at a time (FR-59.2).</summary>
    public virtual async Task<RecoveryResult> RecoverAsync(string sessionName, ParsedSessionStatus? status, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_running)
        {
            if (!_running.Add(sessionName))
                return new RecoveryResult(false, BusyStep, "A recovery is already running for this session.");
            _active.Add(done.Task);
        }

        // StopAsync (a reload of the configuration) cancels the run through this linked token too.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;

        try
        {
            var problem = CheckRecoverable(status, out var analysis);
            if (problem is not null)
            {
                _logger.LogWarning("Recovery of '{Session}' refused: {Problem}", sessionName, problem);
                return new RecoveryResult(false, "check", problem);
            }

            var emptied = analysis.Emptied!;
            var purged = analysis.Purged!;
            _logger.LogInformation(
                "Recovery of '{Session}': {Emptied} was emptied; purging {Purged} ({Files} file(s) announced), then resetting the session",
                sessionName, emptied.Url, purged.Url, analysis.PurgedFileCount);

            try
            {
                var purge = await _purger.PurgeAsync(purged, cancellationToken);
                _logger.LogInformation("Recovery of '{Session}': purge done on '{Root}'", sessionName, purge.ResolvedRoot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Recovery of '{Session}': the purge of {Purged} failed", sessionName, purged.Url);
                return new RecoveryResult(false, "purge", ex.Message);
            }

            try
            {
                await _cliClient.ResetSessionAsync(sessionName, cancellationToken);
                _logger.LogInformation("Recovery of '{Session}': session reset", sessionName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The purged side is already empty here: say so, the halt may come back on the next restart.
                _logger.LogError(ex, "Recovery of '{Session}': the reset failed after {Purged} was purged", sessionName, purged.Url);
                return new RecoveryResult(false, "reset", ex.Message);
            }

            return new RecoveryResult(true, "done", $"{purged.Url} purged and session reset.");
        }
        finally
        {
            lock (_running)
            {
                _running.Remove(sessionName);
                _active.Remove(done.Task);

                // Whoever ran it: a `Halted` listing that began before this instant may be the one this run resolved.
                _lastCompletedUtc[sessionName] = DateTimeOffset.UtcNow;
            }

            done.SetResult();
        }
    }
}

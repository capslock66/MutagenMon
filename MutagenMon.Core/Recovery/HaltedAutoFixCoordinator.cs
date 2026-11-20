using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Notifications;
using MutagenMon.Core.Sessions;

namespace MutagenMon.Core.Recovery;

/// <summary>
/// The automatic resolution of root-emptying halts (FR-61) and the halt
/// notifications (FR-62). Called once per poll by
/// <see cref="Monitoring.SessionMonitorService"/>; it never blocks the poll
/// loop (FR-59.2): a recovery runs on a background task.
///
/// State is kept per *halt episode* of each session: from the first poll that
/// reports a `Halted…` status to the first poll that does not. It holds the
/// number of consecutive failures (FR-61.4) and whether the halt was already
/// notified (once per episode, FR-62). The time of the last automatic run is
/// NOT part of the episode: the cooldown (FR-61.3) is a minimum delay between
/// two runs on the same session, whatever happens in between, so a session
/// that halts again right after a fix does not trigger a purge + reset loop.
/// </summary>
public sealed class HaltedAutoFixCoordinator : IDisposable
{
    private const int MaxConsecutiveFailures = 3;

    private readonly bool _globalAutoFix;
    private readonly string _rootEmptyingText;
    private readonly TimeSpan _cooldown;
    private readonly IReadOnlyDictionary<string, SyncMode> _modes;
    private readonly HaltedRecoveryService? _recovery;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Episode> _episodes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunUtc = new();
    private readonly List<Task> _runs = new();

    // The per-session `@halted-auto-fix` overrides (FR-64). Replaced as a whole, so a poll always sees a consistent
    // map even when an Edit changes a directive meanwhile (FR-65.3).
    private volatile IReadOnlyDictionary<string, bool> _overrides;

    /// <param name="recovery">Null disables the automatic fix (the halts are still notified).</param>
    /// <param name="clock">Test seam for the cooldown; defaults to the UTC clock.</param>
    public HaltedAutoFixCoordinator(
        MutagenMonOptions options, IEnumerable<SessionDefinition> sessions, HaltedRecoveryService? recovery,
        NotificationDispatcher dispatcher, ILogger logger, Func<DateTimeOffset>? clock = null)
    {
        _globalAutoFix = options.HaltedOneSidedRootEmptyingAutoFix;
        _rootEmptyingText = options.HaltedOneSidedRootEmptying;
        _cooldown = TimeSpan.FromSeconds(Math.Max(0, options.HaltedOneSidedRootEmptyingCooldownSeconds));
        _recovery = recovery;
        _dispatcher = dispatcher;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        var definitions = sessions.ToList();
        _modes = definitions.ToDictionary(s_ => s_.Name, s_ => SessionCommandLineParser.Parse(s_.RawCreateCommand).Mode);
        _overrides = definitions.Where(s_ => s_.HaltedAutoFix is not null).ToDictionary(s_ => s_.Name, s_ => s_.HaltedAutoFix!.Value);
    }

    private sealed class Episode
    {
        public bool HaltNotified;
        public bool RunInFlight;
        public bool GaveUp;
        public bool Ended;
        public bool PendingPersistCheck;
        public int Failures;
        public string LastError = "";
    }

    /// <summary>FR-65.3: replaces the per-session overrides without
    /// restarting anything — an Edit that only changed a session's
    /// `@halted-auto-fix` directive takes effect on the next poll.</summary>
    public void SetOverrides(IReadOnlyDictionary<string, bool> overrides) => _overrides = overrides;

    /// <summary>Looks at the sessions of one poll. <paramref name="monitoringEnabled"/>
    /// is false while the user has paused monitoring (FR-61.1.2).</summary>
    /// <param name="pollStartedUtc">When the poll began, before its `mutagen sync list` ran: a halted
    /// listing that began before the last recovery of the session finished may be the very halt that
    /// recovery resolved, and is ignored. Null means "now" (no such check).</param>
    public void Evaluate(
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses, bool monitoringEnabled, DateTimeOffset? pollStartedUtc = null)
    {
        foreach (var (name, mode) in _modes)
        {
            statuses.TryGetValue(name, out var status);
            var analysis = HaltedSessionAnalyzer.Analyze(status, _rootEmptyingText);
            if (analysis.Kind == HaltKind.None)
            {
                // The halt episode is over: counters and "already notified" start afresh. A run still in flight keeps a
                // reference to the old episode and must not report on it any more.
                if (_episodes.Remove(name, out var ended))
                    lock (ended)
                        ended.Ended = true;
                continue;
            }

            // Acting on such a listing could purge a session that synchronises again (whoever ran that recovery).
            if (pollStartedUtc is { } started && _recovery?.ListingPredatesLastRecovery(name, started) == true)
                continue;

            if (!_episodes.TryGetValue(name, out var episode))
                _episodes[name] = episode = new Episode();

            if (analysis.Kind == HaltKind.Other)
                NotifyHaltOnce(name, status!, episode, "this halt is never resolved by MutagenMon");
            else
                EvaluateRootEmptying(name, mode, status!, monitoringEnabled, episode);
        }
    }

    private void EvaluateRootEmptying(string name, SyncMode mode, ParsedSessionStatus status, bool monitoringEnabled, Episode episode)
    {
        // FR-62.4: while a recovery of this session runs, nothing is notified, whatever changes meanwhile.
        lock (episode)
        {
            if (episode.RunInFlight || (_recovery?.IsRunning(name) ?? false))
                return;
        }

        var blocker = FindBlocker(name, mode, status, monitoringEnabled);
        if (blocker is not null)
        {
            NotifyHaltOnce(name, status, episode, blocker);
            return;
        }

        DateTimeOffset? previousRun;
        lock (episode)
        {
            // A manual recovery of the same session may be running (FR-59.2): wait for it, it is not a failure.
            if (episode.GaveUp || episode.RunInFlight || _recovery!.IsRunning(name))
                return;

            var now = _clock();
            previousRun = _lastRunUtc.TryGetValue(name, out var lastRun) ? lastRun : null;
            if (previousRun is { } last && now - last < _cooldown)
                return;

            // A run that completed, and the halt still (or again) reported after the cooldown with no clean poll in
            // between: that run failed (FR-61.4).
            if (episode.PendingPersistCheck)
            {
                episode.PendingPersistCheck = false;
                episode.Failures++;
                episode.LastError = "the session is still halted after the automatic fix";
            }

            if (episode.Failures >= MaxConsecutiveFailures)
            {
                GiveUp(name, episode);
                return;
            }

            episode.RunInFlight = true;
            _lastRunUtc[name] = now;
        }

        StartRun(name, status, episode, previousRun);
    }

    /// <summary>The reason the automatic fix will not run for this halt, or
    /// null when every condition of FR-61.1 holds.</summary>
    private string? FindBlocker(string name, SyncMode mode, ParsedSessionStatus status, bool monitoringEnabled)
    {
        var effective = _overrides.TryGetValue(name, out var sessionFlag) ? sessionFlag : _globalAutoFix;
        if (!effective)
            return "the automatic fix is disabled for this session";
        if (!monitoringEnabled)
            return "monitoring is paused";
        if (mode != SyncMode.TwoWaySafe)
            return $"the sync mode is {mode}, not two-way-safe";
        if (_recovery is null)
            return "no recovery service is available";
        return _recovery.CheckRecoverable(status, out _) is { } problem ? problem : null;
    }

    /// <summary>FR-62.1: the halt will not be resolved automatically — tell the
    /// user once per halt episode.</summary>
    private void NotifyHaltOnce(string name, ParsedSessionStatus status, Episode episode, string reason)
    {
        if (episode.HaltNotified)
            return;

        episode.HaltNotified = true;
        _logger.LogInformation("Session '{Name}' is halted and will not be fixed automatically: {Reason}", name, reason);
        _dispatcher.NotifyHalted(name, status.Status);
    }

    private void StartRun(string name, ParsedSessionStatus status, Episode episode, DateTimeOffset? previousRun)
    {
        _logger.LogInformation("Automatic fix of halted session '{Name}' starting", name);
        var task = Task.Run(async () =>
        {
            RecoveryResult result;
            try
            {
                result = await _recovery!.RecoverAsync(name, status, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                lock (episode)
                    episode.RunInFlight = false;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic fix of halted session '{Name}' failed unexpectedly", name);
                result = new RecoveryResult(false, "unexpected", ex.Message);
            }

            lock (episode)
            {
                episode.RunInFlight = false;

                if (result.Step == HaltedRecoveryService.BusyStep)
                {
                    // A manual recovery got in first: nothing was run, so neither a failure nor a cooldown to serve.
                    // Done even when the episode ended: this run is the only one that set the value.
                    if (previousRun is { } before)
                        _lastRunUtc[name] = before;
                    else
                        _lastRunUtc.TryRemove(name, out _);
                    return;
                }

                if (episode.Ended)
                    return;

                if (result.Success)
                {
                    episode.PendingPersistCheck = true;
                    return;
                }

                episode.Failures++;
                episode.LastError = $"{result.Step}: {result.Message}";
                _logger.LogWarning(
                    "Automatic fix of '{Name}' failed ({Failures}/{Max}): {Error}", name, episode.Failures, MaxConsecutiveFailures, episode.LastError);
                if (episode.Failures >= MaxConsecutiveFailures)
                    GiveUp(name, episode);
            }
        });

        lock (_runs)
        {
            _runs.RemoveAll(t_ => t_.IsCompleted);
            _runs.Add(task);
        }
    }

    /// <summary>FR-61.4/FR-62.3: three consecutive failures — stop for this
    /// halt and notify, once. Called with the episode locked.</summary>
    private void GiveUp(string name, Episode episode)
    {
        if (episode.GaveUp)
            return;

        episode.GaveUp = true;
        _logger.LogError("Automatic fix of '{Name}' abandoned after {Max} consecutive failures: {Error}", name, MaxConsecutiveFailures, episode.LastError);
        _dispatcher.NotifyAutoFixFailed(name, episode.LastError);
    }

    /// <summary>Completes when every recovery started so far has finished (for tests and an orderly shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        Task[] runs;
        lock (_runs)
            runs = _runs.ToArray();

        foreach (var run in runs)
        {
            try
            {
                await run;
            }
            catch (Exception ex)
            {
                // The run already handles its own failures; this only waits for it to end, and a reload must not fail on it.
                _logger.LogError(ex, "A halted-session recovery task ended with an unexpected exception");
            }
        }
    }

    private int _disposed;

    /// <summary>Cancels the recoveries still running (monitor stopped or reloaded). Safe to call twice: the host
    /// disposes the initial monitor again when the application exits.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _shutdown.Cancel();
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Notifications;
using MutagenMon.Core.ProfileWatch;
using MutagenMon.Core.Resolution;
using MutagenMon.Core.Sessions;
using MutagenMon.Core.Status;
using TraceTool;

namespace MutagenMon.Core.Monitoring;

/// <summary>
/// Implements the polling loop, including the per-session automatic-restart
/// pass (FR-13). Every poll: calls the mutagen CLI, parses, classifies every known
/// session while tracking the single worst code across them, runs the
/// auto-resolve pass (FR-10) over the freshly-parsed conflicts, checks for
/// profile updates (FR-12), queues any desktop notification call for (new
/// conflicts FR-11.1, auto-resolve FR-11.2, confirmed profile update
/// FR-11.4, automatic restarts FR-11.3), restarts or terminates unhealthy
/// sessions (FR-13/FR-7.2), and publishes one immutable
/// <see cref="MonitorSnapshot"/>.
///
/// <see cref="PollOnceAsync"/> is exposed (not just the BackgroundService's
/// internal timer loop) so tests can drive the whole pipeline deterministically
/// through a fake <see cref="IMutagenCliClient"/> without a real
/// `mutagen` binary (NFR-11).
/// </summary>
public sealed class SessionMonitorService : BackgroundService
{
    private readonly IMutagenCliClient _cliClient;
    private readonly ISessionStateStore _stateStore;
    private readonly IReadOnlyList<string> _sessionNames;
    private readonly IReadOnlyDictionary<string, SessionDefinition> _sessionDefinitionsByName;
    private readonly SessionStateTracker _tracker = new();
    private readonly SessionProfileWatcher _profileWatcher;
    private readonly AutoResolveEngine _autoResolveEngine;
    private readonly ConflictNotificationTracker _conflictNotificationTracker = new();
    private readonly NotificationDispatcher _notificationDispatcher;
    private readonly RestartLogWriter _restartLogWriter;
    private readonly int _sessionMaxNoSession;
    private readonly int _sessionMaxDuplicate;
    private readonly int _sessionMaxErrors;
    private readonly TimeSpan _pollPeriod;
    private volatile bool _enabled;
    private readonly ILogger<SessionMonitorService> _logger;
    private (SessionStatusCode Worst, bool Enabled, bool ProfileUpdated)? _lastLoggedPollState;

    /// <summary>Current monitoring-enabled state (FR-7.2). Read each poll to
    /// decide whether to actively terminate sessions.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Enables or disables monitoring (FR-7.2). Disabling takes effect on the next poll —
    /// see <see cref="PollOnceAsync"/>'s termination pass. Enabling does not
    /// itself restart anything: reviving a missing/stopped session still
    /// requires its own abnormal-poll threshold to be crossed again (FR-13).</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;
        _logger.LogInformation("Monitoring {State}", enabled ? "enabled" : "disabled");
        _enabled = enabled;
    }

    public SessionMonitorService(
        IMutagenCliClient cliClient,
        ISessionStateStore stateStore,
        IOptions<MutagenMonOptions> options,
        IReadOnlyList<SessionDefinition> sessions,
        IFileTimestampProvider timestampProvider,
        ConflictResolutionService conflictResolutionService,
        INotificationQueue notificationQueue,
        RestartLogWriter restartLogWriter,
        ILogger<SessionMonitorService> logger)
    {
        _cliClient = cliClient;
        _stateStore = stateStore;
        _sessionNames = sessions.Select(s => s.Name).ToArray();
        _sessionDefinitionsByName = sessions.ToDictionary(s => s.Name);
        _restartLogWriter = restartLogWriter;
        _logger = logger;

        var opts = options.Value;
        _pollPeriod = TimeSpan.FromMilliseconds(opts.MutagenPollPeriodMs);
        _enabled = opts.StartEnabled;
        _sessionMaxNoSession = opts.SessionMaxNoSession;
        _sessionMaxDuplicate = opts.SessionMaxDuplicate;
        _sessionMaxErrors = opts.SessionMaxErrors;
        _profileWatcher = new SessionProfileWatcher(
            timestampProvider, opts.MutagenProfileDir, opts.MutagenProfileDirWatchPeriod, opts.MutagenProfileGraceSeconds);
        _autoResolveEngine = new AutoResolveEngine(
            opts.AutoResolve, TimeSpan.FromSeconds(opts.AutoResolveHistoryAgeSeconds), conflictResolutionService);
        _notificationDispatcher = new NotificationDispatcher(
            notificationQueue, opts.NotifyConflicts, opts.NotifyAutoresolve, opts.NotifyMutagenProfileUpdate,
            opts.NotifyRestartConnection);
        _autoResolveEngine.ConflictAutoResolved += (_, e) =>
        {
            _logger.LogInformation("Auto-resolved conflict {Session}:{File} via rule '{Rule}'", e.SessionName, e.FileName, e.Rule);
            _notificationDispatcher.NotifyAutoResolved(e.SessionName, e.FileName, e.Rule);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Session monitor loop starting: {SessionCount} session(s), poll period {PollPeriod}",
            _sessionNames.Count, _pollPeriod);
        using var timer = new PeriodicTimer(_pollPeriod);
        do
        {
            await PollOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _cliClient.GetSyncListRawAsync(cancellationToken);

            // log the mutagen sync list output. Uncomment if needed for debugging.
            // _logger.LogInformation(raw);

            // RawLog,SessionStatuses dic,Conflicts dic
            MutagenSyncListResult parsed = MutagenSyncListParser.Parse(raw, _sessionNames);

            // Tracks the single worst session status (FR-4: the tray icon
            // always reflects the single worst session). Lower enum value =
            // worse; defaults to Ready when there are no configured
            // sessions, since there's nothing worse to report.
            
            // SessionStatusCode: ConnectionError = -2,NotRunning = -1,Unknown = 0,Scanning = 30,Syncing = 40,Problems = 50,Conflicts = 60,Ready = 100
            var worst = SessionStatusCode.Ready;
            foreach (var name in _sessionNames)
            {
                parsed.SessionStatuses.TryGetValue(name, out var status);

                // log the mutagen session status output. Uncomment if needed for debugging. 
                // _logger.LogInformation($"Session '{name}' status: {status}");

                var code = _tracker.Update(name, status);
                if (code < worst)
                    worst = code;
            }

            var profileUpdated = _profileWatcher.Tick(parsed.SessionStatuses);
            _notificationDispatcher.NotifyProfileUpdated(_profileWatcher.ConfirmedUpdatedSessions);

            var nowUtc = DateTimeOffset.UtcNow;
            var conflicts = await _autoResolveEngine.ApplyAsync(parsed.Conflicts, parsed.SessionStatuses, nowUtc, cancellationToken);

            var newConflictKeys = _conflictNotificationTracker.DetectNew(conflicts, worst);
            _notificationDispatcher.NotifyNewConflicts(newConflictKeys);

            var pollState = (worst, _enabled, profileUpdated);
            if (_lastLoggedPollState != pollState)
            {
                _logger.LogInformation(
                    "Poll succeeded: worst={Worst}, enabled={Enabled}, profileUpdated={ProfileUpdated}",
                    worst, _enabled, profileUpdated);
                _lastLoggedPollState = pollState;
            }

            _stateStore.Publish(new MonitorSnapshot(
                worst,
                _enabled,
                profileUpdated,
                nowUtc,
                parsed.RawLog,
                parsed.SessionStatuses,
                conflicts));

            if (_enabled)
                await RestartUnhealthySessionsAsync(parsed.SessionStatuses, parsed.RawLog, nowUtc, cancellationToken);
            else
                await TerminateRunningSessionsAsync(parsed.SessionStatuses, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Mutagen status poll failed; keeping the last published snapshot and retrying next cycle");
        }
    }

    /// <summary>Implements the automatic-restart pass (FR-13). For each
    /// known session, re-derives which (if any) of the
    /// three abnormal causes currently applies from this same poll's status
    /// and the shared consecutive-miss counter already updated by
    /// <see cref="SessionStateTracker.Update"/> above, and restarts the
    /// session once its cause-specific threshold is exceeded.</summary>
    private async Task RestartUnhealthySessionsAsync(
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses, string rawLog, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        foreach (var name in _sessionNames)
        {
            statuses.TryGetValue(name, out var status);
            var misses = _tracker.GetConsecutiveMisses(name);

            string cause;
            bool notifyAlways = false, notifyIfConnectingEnabled = false;

            if (status is null)
            {
                if (misses <= _sessionMaxNoSession)
                    continue;
                cause = "Restarting";
            }
            else if (status.IsDuplicate)
            {
                if (misses <= _sessionMaxDuplicate)
                    continue;
                cause = "Restarting duplicate";
                notifyAlways = true;
            }
            else if (SessionStateTracker.ConnectingPrefixes.Any(p => status.Status.StartsWith(p, StringComparison.Ordinal)))
            {
                if (misses <= _sessionMaxErrors)
                    continue;
                cause = "Restarting connection";
                notifyIfConnectingEnabled = true;
            }
            else
                continue;

            _logger.LogWarning("{Cause}: {SessionName} (stuck for {Misses} consecutive poll(s))", cause, name, misses);
            _restartLogWriter.Append(name, rawLog, cause, nowUtc);

            await RestartSessionAsync(name, sessionExists: status is not null, cancellationToken);
            _tracker.ResetConsecutiveMisses(name);

            if (notifyAlways)
                _notificationDispatcher.NotifyRestartedForDuplicate(name, status!.Status);
            else if (notifyIfConnectingEnabled)
                _notificationDispatcher.NotifyRestartedForConnecting(name, status!.Status);
        }
    }

    /// <summary>Restarts a session (FR-13.5) by terminating then recreating
    /// it from its original definition, each step independently tolerant of the other's
    /// failure. <paramref name="sessionExists"/> skips the terminate step
    /// when the session is already absent (missing-session restart cause),
    /// avoiding a guaranteed-to-fail CLI call.</summary>
    private async Task RestartSessionAsync(string sessionName, bool sessionExists, CancellationToken cancellationToken)
    {
        if (sessionExists)
            try
            {
                await _cliClient.TerminateSessionAsync(sessionName, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to terminate session '{SessionName}' during automatic restart", sessionName);
            }

        try
        {
            await _cliClient.CreateSessionAsync(_sessionDefinitionsByName[sessionName].RawCreateCommand, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to recreate session '{SessionName}' during automatic restart", sessionName);
        }
    }

    /// <summary>While monitoring is disabled, every session that still
    /// reports a status is actively terminated. Per-session failures are
    /// logged and swallowed so one unreachable session
    /// doesn't stop the others from being terminated.</summary>
    private async Task TerminateRunningSessionsAsync(
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses, CancellationToken cancellationToken)
    {
        foreach (var name in _sessionNames)
        {
            if (!statuses.TryGetValue(name, out var status) || status is null || string.IsNullOrEmpty(status.Status))
                continue;

            try
            {
                await _cliClient.TerminateSessionAsync(name, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to terminate session '{SessionName}' while monitoring is disabled", name);
            }
        }
    }
}

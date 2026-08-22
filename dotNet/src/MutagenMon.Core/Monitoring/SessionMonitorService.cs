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
/// Ports mutagenmonlib/remote/monitor.py: Monitor's polling loop (the
/// per-session restart pieces of that class are out of scope for this
/// phase — see requirements/05-wpf-migration-notes.md §6 Phase 5, FR-13).
/// Every poll: calls the mutagen CLI, parses, classifies every known
/// session while tracking the single worst code across them, runs the
/// auto-resolve pass (FR-10) over the freshly-parsed conflicts, checks for
/// profile updates (FR-12), queues any desktop notification call for (new
/// conflicts FR-11.1, auto-resolve FR-11.2, confirmed profile update
/// FR-11.4), and publishes one immutable <see cref="MonitorSnapshot"/>.
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
    private readonly SessionStateTracker _tracker = new();
    private readonly SessionProfileWatcher _profileWatcher;
    private readonly AutoResolveEngine _autoResolveEngine;
    private readonly ConflictNotificationTracker _conflictNotificationTracker = new();
    private readonly NotificationDispatcher _notificationDispatcher;
    private readonly TimeSpan _pollPeriod;
    private volatile bool _enabled;
    private readonly ILogger<SessionMonitorService> _logger;

    /// <summary>Current monitoring-enabled state (FR-7.2). Read each poll to
    /// decide whether to actively terminate sessions.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Ports mutagenmonlib/remote/monitor.py: StartMutagen()/
    /// DisableMutagen() (FR-7.2). Disabling takes effect on the next poll —
    /// see <see cref="PollOnceAsync"/>'s termination pass. Enabling does not
    /// itself restart anything: reviving missing/stopped sessions is the
    /// auto-recovery logic (FR-13, Phase 5), not yet implemented.</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
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
        ILogger<SessionMonitorService> logger)
    {
        _cliClient = cliClient;
        _stateStore = stateStore;
        _sessionNames = sessions.Select(s => s.Name).ToArray();
        _logger = logger;

        var opts = options.Value;
        _pollPeriod = TimeSpan.FromMilliseconds(opts.MutagenPollPeriodMs);
        _enabled = opts.StartEnabled;
        _profileWatcher = new SessionProfileWatcher(
            timestampProvider, opts.MutagenProfileDir, opts.MutagenProfileDirWatchPeriod, opts.MutagenProfileGraceSeconds);
        _autoResolveEngine = new AutoResolveEngine(
            opts.AutoResolve, TimeSpan.FromSeconds(opts.AutoResolveHistoryAgeSeconds), conflictResolutionService);
        _notificationDispatcher = new NotificationDispatcher(
            notificationQueue, opts.NotifyConflicts, opts.NotifyAutoresolve, opts.NotifyMutagenProfileUpdate);
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
            
            // log the mutagen sync list output
            _logger.LogInformation(raw);
            
            // RawLog,SessionStatuses dic,Conflicts dic
            MutagenSyncListResult parsed = MutagenSyncListParser.Parse(raw, _sessionNames);

            // Ports mutagenmonlib/remote/mutagen.py: get_worst_code() (FR-4: the
            // tray icon always reflects the single worst session). Lower enum
            // value = worse; legacy get_worst_code() starts worst_code=100 and
            // never lowers it if there are no configured sessions, hence the
            // Ready default here.
            
            // SessionStatusCode: ConnectionError = -2,NotRunning = -1,Unknown = 0,Scanning = 30,Syncing = 40,Problems = 50,Conflicts = 60,Ready = 100
            var worst = SessionStatusCode.Ready;
            foreach (var name in _sessionNames)
            {
                parsed.SessionStatuses.TryGetValue(name, out var status);

                _logger.LogInformation($"Session '{name}' status: {status}");

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

            _logger.LogInformation(
                "Poll succeeded: worst={Worst}, enabled={Enabled}, profileUpdated={ProfileUpdated}",
                worst, _enabled, profileUpdated);

            _stateStore.Publish(new MonitorSnapshot(
                worst,
                _enabled,
                profileUpdated,
                nowUtc,
                parsed.RawLog,
                parsed.SessionStatuses,
                conflicts));

            if (!_enabled)
            {
                await TerminateRunningSessionsAsync(parsed.SessionStatuses, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Mutagen status poll failed; keeping the last published snapshot and retrying next cycle");
        }
    }

    /// <summary>Ports mutagenmonlib/remote/monitor.py: stop_mutagen() — while
    /// monitoring is disabled, every session that still reports a status is
    /// actively terminated. Per-session failures are logged and swallowed
    /// (mirrors the legacy's bare except/pass) so one unreachable session
    /// doesn't stop the others from being terminated.</summary>
    private async Task TerminateRunningSessionsAsync(
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses, CancellationToken cancellationToken)
    {
        foreach (var name in _sessionNames)
        {
            if (!statuses.TryGetValue(name, out var status) || status is null || string.IsNullOrEmpty(status.Status))
            {
                continue;
            }

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

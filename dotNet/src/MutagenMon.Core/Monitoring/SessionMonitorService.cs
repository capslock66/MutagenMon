using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.ProfileWatch;
using MutagenMon.Core.Sessions;
using MutagenMon.Core.Status;
using TraceTool;

namespace MutagenMon.Core.Monitoring;

/// <summary>
/// Ports mutagenmonlib/remote/monitor.py: Monitor's polling loop (the
/// restart/auto-resolve/notification pieces of that class are out of scope
/// for this phase — see requirements/05-wpf-migration-notes.md §6
/// Phases 3/4/5). Every poll: calls the mutagen CLI, parses, classifies
/// every known session while tracking the single worst code across them,
/// checks for profile updates, and publishes one immutable
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
    private readonly SessionStateTracker _tracker = new();
    private readonly SessionProfileWatcher _profileWatcher;
    private readonly TimeSpan _pollPeriod;
    private readonly bool _enabled;
    private readonly ILogger<SessionMonitorService> _logger;

    public SessionMonitorService(
        IMutagenCliClient cliClient,
        ISessionStateStore stateStore,
        IOptions<MutagenMonOptions> options,
        IReadOnlyList<SessionDefinition> sessions,
        IFileTimestampProvider timestampProvider,
        ILogger<SessionMonitorService> logger)
    {
        _cliClient = cliClient;
        _stateStore = stateStore;
        _sessionNames = sessions.Select(s => s.Name).ToArray();
        _logger = logger;

        var opts = options.Value;
        _pollPeriod = TimeSpan.FromMilliseconds(opts.MutagenPollPeriodMs);
        _enabled = opts.StartEnabled;
        _profileWatcher = new SessionProfileWatcher(timestampProvider, opts.MutagenProfileDir, opts.MutagenProfileDirWatchPeriod);
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

            foreach (var (name,status) in parsed.SessionStatuses)        // KeyValuePair<string, ParsedSessionStatus?>
            {
                _logger.LogInformation($"Session '{name}' status: {status.Status}, hasProblem: {status.HasProblems}, HasConflicts: {status.HasConflicts}");
                //TTrace.Debug.Send($"Session '{name}' status: {status}");

                var code = _tracker.Update(name, status);
                if (code < worst) 
                    worst = code;
                
            }



            var profileUpdated = _profileWatcher.Tick(parsed.SessionStatuses);

            _logger.LogInformation(
                "Poll succeeded: worst={Worst}, enabled={Enabled}, profileUpdated={ProfileUpdated}",
                worst, _enabled, profileUpdated);

            _stateStore.Publish(new MonitorSnapshot(
                worst,
                _enabled,
                profileUpdated,
                DateTimeOffset.UtcNow,
                parsed.RawLog,
                parsed.SessionStatuses,
                parsed.Conflicts));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Mutagen status poll failed; keeping the last published snapshot and retrying next cycle");
        }
    }
}

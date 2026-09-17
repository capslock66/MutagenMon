using System.Text.RegularExpressions;
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
/// pass (FR-13). Every poll: calls the mutagen CLI, parses (a pure port of
/// the legacy session-status parsing logic, including the text cleanup step
/// performed before classification — see NFR-11), classifies every known
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
/// through a fake <see cref="MutagenCliClient"/> without a real
/// `mutagen` binary (NFR-11) — and <see cref="Parse"/> itself is `internal`
/// (not `private`) so the CLI-output-parsing edge cases can still be tested
/// directly, without going through that whole pipeline, via this assembly's
/// <c>InternalsVisibleTo("MutagenMon.Core.Tests")</c>.
/// </summary>
public sealed partial class SessionMonitorService : BackgroundService
{
    private readonly MutagenCliClient _cliClient;
    private readonly SessionStateStore _stateStore;
    private readonly IReadOnlyList<string> _sessionNames;
    private readonly IReadOnlyDictionary<string, SessionDefinition> _sessionDefinitionsByName;
    private readonly SessionStateTracker _tracker = new();
    private readonly SessionProfileWatcher _profileWatcher;
    private readonly AutoResolveEngine _autoResolveEngine;
    private readonly ConflictNotificationTracker _conflictNotificationTracker = new();
    private readonly NotificationDispatcher _notificationDispatcher;
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
        MutagenCliClient cliClient,
        SessionStateStore stateStore,
        IOptions<MutagenMonOptions> options,
        IReadOnlyList<SessionDefinition> sessions,
        FileTimestampProvider timestampProvider,
        ConflictResolutionService conflictResolutionService,
        NotificationQueue notificationQueue,
        ILogger<SessionMonitorService> logger)
    {
        _cliClient = cliClient;
        _stateStore = stateStore;
        _sessionNames = sessions.Select(s => s.Name).ToArray();
        _sessionDefinitionsByName = sessions.ToDictionary(s => s.Name);
        _logger = logger;

        var opts = options.Value;
        _pollPeriod = TimeSpan.FromMilliseconds(opts.MutagenPollPeriodMs);
        _enabled = opts.StartEnabled;
        _sessionMaxNoSession = opts.SessionMaxNoSession;
        _sessionMaxDuplicate = opts.SessionMaxDuplicate;
        _sessionMaxErrors = opts.SessionMaxErrors;
        _profileWatcher = new SessionProfileWatcher(timestampProvider, opts.MutagenProfileDir, opts.MutagenProfileGraceSeconds);
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
            //_logger.LogInformation(raw);

            // RawLog,SessionStatuses dic,Conflicts dic
            MutagenSyncListResult parsed = Parse(raw, _sessionNames);

            // Tracks the single worst session status (FR-4: the tray icon
            // always reflects the single worst session). Lower enum value =
            // worse; defaults to Ready when there are no configured
            // sessions, since there's nothing worse to report.
            
            // SessionStatusCode: ConnectionError = -2,NotRunning = -1,Unknown = 0,Scanning = 30,Syncing = 40,Problems = 50,Conflicts = 60,Ready = 100
            var worst = SessionStatusCode.Ready;
            var sessionCodes = new Dictionary<string, SessionStatusCode>();
            foreach (var name in _sessionNames)
            {
                parsed.SessionStatuses.TryGetValue(name, out var status);

                if (status?.Staging is { } staging)
                    _logger.LogInformation(
                        "Session '{Name}' staging: {FilesCompleted}/{FilesTotal} files, {BytesTransferred} ({PercentComplete}%){CurrentFile}",
                        name, staging.FilesCompleted, staging.FilesTotal, staging.BytesTransferred, staging.PercentComplete,
                        staging.CurrentFileName is null ? "" : $" — current file: {staging.CurrentFileName} ({staging.CurrentFileBytesTransferred}/{staging.CurrentFileTotalBytes})");

                var code = _tracker.Update(name, status);
                sessionCodes[name] = code;
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
                conflicts,
                _profileWatcher.LastSeenMtimeUtc,
                sessionCodes));

            if (_enabled)
                await RestartUnhealthySessionsAsync(parsed.SessionStatuses, parsed.RawLog, cancellationToken);
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
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses, string rawLog, CancellationToken cancellationToken)
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

            _logger.LogWarning(
                "{Cause}: {SessionName} (stuck for {Misses} consecutive poll(s)). Snapshot: {RawStatusSnapshot}",
                cause, name, misses, rawLog);

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

    /// <summary>
    /// Deliberate deviation from the legacy behavior: if the CLI output contains
    /// a session name that is not in <paramref name="knownSessionNames"/> (a
    /// stray/orphaned mutagen session not declared in the sessions file), the
    /// legacy implementation raised an error there, which its caller silently
    /// swallowed — meaning ALL status polling for EVERY session silently stops
    /// every cycle as long as the stray session exists. This port instead just
    /// carries the extra entry along in the result (ignored by anything that
    /// only iterates known session names); this is a robustness fix, not a
    /// documented requirement change.
    /// </summary>
    internal static MutagenSyncListResult Parse(string rawOutput, IReadOnlyCollection<string> knownSessionNames)
    {
        var cleaned = Normalize(rawOutput);

        var builders = new Dictionary<string, ParseBuilder>();
        var conflicts = new Dictionary<string, List<ConflictRecord>>();
        foreach (var name in knownSessionNames)
            conflicts[name] = new List<ConflictRecord>();

        var currentName = "";
        var side = 0;
        var pendingAlphaName = "";
        var pendingAlphaState = "";

        foreach (var rawLine in cleaned.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("Name: ", StringComparison.Ordinal))
            {
                currentName = line[6..];
                var b = GetOrCreate(builders, currentName);
                b.IsDuplicate = b.Touched;
                b.Touched = true;
                b.HasConflicts = false;
                b.HasProblems = false;
                if (!conflicts.ContainsKey(currentName))
                    conflicts[currentName] = new List<ConflictRecord>();
                continue;
            }

            if (currentName.Length == 0)
                continue; // stray line before any "Name:" seen

            var current = GetOrCreate(builders, currentName);

            if (line.StartsWith("Identifier: ", StringComparison.Ordinal))
                current.Id = line[12..];
            else if (line.StartsWith("Status: ", StringComparison.Ordinal))
                current.Status = line[8..];
            else if (line.StartsWith("Staging progress: ", StringComparison.Ordinal))
                current.Staging = ParseStagingProgressLine(line[18..]);
            else if (line.StartsWith("Current file: ", StringComparison.Ordinal))
                current.Staging = ApplyCurrentFileLine(current.Staging, line[14..]);
            else if (line.StartsWith("Alpha:", StringComparison.Ordinal))
                side = 1;
            else if (line.StartsWith("Beta:", StringComparison.Ordinal))
                side = 2;
            else if (line.StartsWith("URL: ", StringComparison.Ordinal))
            {
                var endpoint = BuildEndpoint(line[5..]);
                if (side == 1)
                    current.Alpha = endpoint;
                else if (side == 2)
                    current.Beta = endpoint;
            }
            else if (line.StartsWith("Conflicts:", StringComparison.Ordinal))
                current.HasConflicts = true;
            else if (line.StartsWith("Problems:", StringComparison.Ordinal))
                current.HasProblems = true;
            else if (line.StartsWith("(alpha) ", StringComparison.Ordinal))
            {
                var pos = FindMatchingOpenParen(line, line.Length - 1);
                if (pos is int p && p > 8)
                {
                    pendingAlphaName = line[8..(p - 1)];
                    pendingAlphaState = line[(p + 1)..];
                }
            }
            else if (line.StartsWith("(beta) ", StringComparison.Ordinal))
            {
                var pos = FindMatchingOpenParen(line, line.Length - 1);
                if (pos is int p && p > 7)
                {
                    var betaName = line[7..(p - 1)];
                    var betaState = line[(p + 1)..];
                    conflicts[currentName].Add(new ConflictRecord(pendingAlphaName, betaName, pendingAlphaState, betaState, AutoResolved: false));
                }
            }
        }

        var statuses = new Dictionary<string, ParsedSessionStatus?>();
        foreach (var name in knownSessionNames)
            statuses[name] = builders.TryGetValue(name, out var b) && b.Touched ? b.ToParsedStatus(name) : null;
        foreach (var (name, b) in builders)
            if (!statuses.ContainsKey(name) && b.Touched)
            {
                statuses[name] = b.ToParsedStatus(name);
                conflicts.TryAdd(name, new List<ConflictRecord>());
            }

        return new MutagenSyncListResult(
            cleaned,
            statuses,
            conflicts.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ConflictRecord>)kv.Value));
    }

    /// <summary>Strips daemon-startup noise and labels lines from the raw CLI
    /// output (a timestamp prefix is a display-only concern for the status view, FR-8 —
    /// out of scope here; RawLog is the cleaned body only).</summary>
    private static string Normalize(string raw)
    {
        var st = raw
            .Replace("Attempting to start Mutagen daemon...", "")
            .Replace("Started Mutagen daemon in background (terminate with \"mutagen daemon stop\")", "")
            .Replace("\r\n", "\n")
            .Replace("\n\t", "\n    ");
        st = LabelsLineRegex().Replace(st, "");
        return st.Trim().Trim('-');
    }

    [GeneratedRegex(@"Labels:.*?\n")]
    private static partial Regex LabelsLineRegex();

    private static ParseBuilder GetOrCreate(Dictionary<string, ParseBuilder> dict, string name)
    {
        if (!dict.TryGetValue(name, out var b))
        {
            b = new ParseBuilder();
            dict[name] = b;
        }
        return b;
    }

    /// <summary>Ports mutagen's own SCP-style-vs-Windows-path disambiguation:
    /// a single character before the first ':' is a Windows drive letter
    /// (e.g. "C:\..." or "C:/..."), not an SSH host. Anything else with a
    /// ':' is "[user@]host:path" — the path may or may not start with '/'
    /// (absolute vs. relative-to-home are both valid SSH specs).
    ///
    /// Bug fix: the previous heuristic required the literal substring ":/"
    /// (i.e. only matched an *absolute* remote path), so a relative-to-home
    /// endpoint like "tparent@pc-ub1:sources/appman" — a real, common mutagen
    /// URL, and exactly the one that surfaced this in production use — was
    /// misclassified as Local. That fed straight into ConflictFileClient's
    /// local-file-IO branch, which then tried to open "sources/appman/..."
    /// as a local path relative to the app's own working directory and threw
    /// an IOException.</summary>
    private static SessionEndpoint BuildEndpoint(string url)
    {
        var colonIndex = url.IndexOf(':', StringComparison.Ordinal);
        return colonIndex > 1
            ? new SessionEndpoint(url, TransportKind.Ssh, url[..colonIndex], url[(colonIndex + 1)..])
            : new SessionEndpoint(url, TransportKind.Local, null, null);
    }

    /// <summary>Parses "0/1 - 34 MB - 0%" (the text after "Staging progress: ")
    /// into completed/total file counts, the cumulative bytes-transferred
    /// text (kept as-is — units vary, no need to parse them for display),
    /// and the percentage. Returns null on any unexpected shape rather than
    /// throwing — a format change upstream should degrade to "no staging
    /// info" instead of breaking the whole poll.</summary>
    private static StagingProgress? ParseStagingProgressLine(string rest)
    {
        var parts = rest.Split(" - ", 3);
        if (parts.Length != 3)
            return null;
        var counts = parts[0].Split('/', 2);
        if (counts.Length != 2 || !int.TryParse(counts[0], out var completed) || !int.TryParse(counts[1], out var total))
            return null;
        if (!int.TryParse(parts[2].TrimEnd('%'), out var percent))
            return null;
        return new StagingProgress(completed, total, parts[1], percent);
    }

    /// <summary>Parses "Tracetool.zip (34 MB/260 MB)" (the text after
    /// "Current file: ") and folds it onto <paramref name="staging"/> — this
    /// line always follows a "Staging progress: " line in practice, but if
    /// it somehow didn't, still produces a <see cref="StagingProgress"/>
    /// with zeroed-out file counts rather than dropping the current-file
    /// detail entirely.</summary>
    private static StagingProgress? ApplyCurrentFileLine(StagingProgress? staging, string rest)
    {
        var openParen = rest.LastIndexOf('(');
        if (openParen < 0 || !rest.EndsWith(')'))
            return staging;
        var name = rest[..openParen].TrimEnd();
        var inside = rest[(openParen + 1)..^1];
        var slash = inside.IndexOf('/');
        if (slash < 0)
            return staging;
        var doneBytes = inside[..slash];
        var totalBytes = inside[(slash + 1)..];
        return staging is null
            ? new StagingProgress(0, 0, "", 0, name, doneBytes, totalBytes)
            : staging with { CurrentFileName = name, CurrentFileBytesTransferred = doneBytes, CurrentFileTotalBytes = totalBytes };
    }

    /// <summary>Finds, scanning backwards from just before <paramref name="closeIndex"/>
    /// (which the caller already knows holds the closing paren to match), the
    /// index of its matching '('.</summary>
    internal static int? FindMatchingOpenParen(string s, int closeIndex)
    {
        var depth = 0;
        for (var x = closeIndex - 1; x >= 0; x--)
            if (s[x] == '(')
            {
                if (depth == 0)
                    return x;
                depth--;
            }
            else if (s[x] == ')')
                depth++;
        return null;
    }

    private sealed class ParseBuilder
    {
        public bool Touched;
        public bool IsDuplicate;
        public string? Id;
        public string Status = "";
        public bool HasProblems;
        public bool HasConflicts;
        public SessionEndpoint? Alpha;
        public SessionEndpoint? Beta;
        public StagingProgress? Staging;

        public ParsedSessionStatus ToParsedStatus(string name) =>
            new(name, Id, Status, IsDuplicate, HasProblems, HasConflicts, Alpha, Beta, Staging);
    }
}

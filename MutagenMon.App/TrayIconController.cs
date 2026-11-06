using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Notifications;
using MutagenMon.Core.Status;

namespace MutagenMon.App;

/// <summary>
/// The App-side half of the tray icon: a 1s DispatcherTimer reads the
/// published <see cref="MonitorSnapshot"/>, computes staleness, resolves the
/// icon/tooltip via <see cref="TrayIconStateResolver"/> (Core), and — only if
/// it actually changed — updates the H.NotifyIcon TaskbarIcon (de-duplicating
/// repeated updates per requirements/03-tray-icon-requirements.md §7 item 4,
/// which is a presentation concern and therefore lives here, not in Core).
/// Also watches for the self-restart threshold (TIC-9/TIC-10), and — every
/// tick — drains any desktop notification (FR-11) queued by the background
/// poller and shows it via <c>TaskbarIcon.ShowNotification</c>; queued
/// messages are only ever consumed here, on the UI thread, matching the
/// pull-based thread-safety pattern already used for
/// <see cref="SessionStateStore"/>.
/// </summary>
public sealed class TrayIconController
{
    /// <summary>Raised on every 1s tick with the latest snapshot and the
    /// freshly-resolved icon/tooltip state — lets the App layer keep an
    /// already-open <c>StatusWindow</c> live (FR-8.4) without this class
    /// knowing that window exists. Fired unconditionally, independent of
    /// the icon/tooltip de-duplication below: a session's raw status text or
    /// its conflict list can change without moving the aggregated icon
    /// state or tooltip wording at all.</summary>
    public event Action<MonitorSnapshot, TrayIconState>? Polled;

    /// <summary>The icon/tooltip state as of the last tick — lets a caller
    /// (e.g. opening the status view on demand) show the same icon the tray
    /// currently displays without duplicating <see cref="TrayIconStateResolver"/>
    /// resolution. Null only before the very first tick.</summary>
    public TrayIconState? CurrentState => _lastState;

    private readonly TaskbarIcon _taskbarIcon;
    private readonly SessionStateStore _stateStore;
    private readonly IconImageCache _iconCache;
    private readonly string _appName;
    private readonly LagThresholds _lagThresholds;
    private readonly IReadOnlyList<string> _sessionNames;
    private readonly NotificationQueue _notificationQueue;
    private readonly Action _onSelfRestartNeeded;
    private readonly Action _onReloadReady;
    private readonly ILogger<TrayIconController> _logger;
    private readonly DispatcherTimer _timer;
    private TrayIconState? _lastState;
    private bool _restartTriggered;
    private bool _reloadRequested;
    private bool _isReopeningContextMenu;
    // Set from the SystemEvents thread (resume), the UI thread (failed
    // update, startup schedule, failed initial creation), consumed by Tick on
    // the UI thread.
    private volatile bool _recreateIconRequested;
    private DateTimeOffset _resumedUtc = DateTimeOffset.MinValue;
    // At auto-start on logon, the icon can be created before Explorer's
    // notification area is ready: it is shown but never receives clicks, and
    // no error or TaskbarCreated message ever signals it. The native icon is
    // therefore recreated every StartupRecreateInterval during the first
    // StartupRecreateWindow after the FIRST controller of the process was
    // created (cheap, idempotent). The window is long because logon on a
    // saturated machine can keep Explorer busy for several minutes (a 6 minute
    // gap was observed). It is anchored on the process, not on this instance:
    // an in-place config reload builds a new controller, and must not replay
    // the logon recreations. Monotonic clock, so sleep or an NTP jump at
    // logon cannot freeze or cut the window short.
    private static readonly TimeSpan StartupRecreateInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupRecreateWindow = TimeSpan.FromMinutes(10);
    // A tick arriving later than this after the previous one means the UI
    // thread was blocked (typically a synchronous Shell_NotifyIcon call
    // waiting on a busy Explorer, or a CPU/disk-saturated logon).
    private static readonly TimeSpan TickLagWarningThreshold = TimeSpan.FromSeconds(5);
    private static long _processFirstControllerTimestamp;
    // After a recreate, the shell is asked where the icon is
    // (Shell_NotifyIconGetRect). A rectangle means the icon is registered in
    // the notification area; it is NOT a proof that mouse messages reach the
    // icon's message window (the failure this mechanism targets), so the value
    // of this signal must be validated against field logs. After this many
    // consecutive confirmations the startup recreations stop. Static like the
    // window anchor: a config reload must not restart the schedule.
    private const int RequiredVisibilityConfirmations = 3;
    private static int _consecutiveVisibilityConfirmations;
    private static volatile bool _startupRecreationConfirmed;
    private TimeSpan _nextStartupRecreateAt;
    private long _lastTickTimestamp;
    // Set from the SystemEvents thread on resume: the next tick gap is the
    // sleep itself, not a blocked UI thread.
    private volatile bool _resumedSinceLastTick;
    // True between Suspend and Resume: unlike the flag above it is not reset
    // by Tick, since ticks can still fire in the seconds before the OS sleeps.
    private volatile bool _isSuspended;
    // A startup recreate deletes the native icon, which can dismiss a
    // balloon still on screen: none is armed shortly after a notification.
    private static readonly TimeSpan NotificationGracePeriod = TimeSpan.FromSeconds(30);
    private static long _lastNotificationTimestamp;
    private bool _startupWindowEndLogged;
    private int _startupRecreatesScheduled;
    // Failed recreate attempts since the last successful one.
    private int _recreateRetryCount;

    /// <summary>True from <see cref="RequestReload"/> until every configured
    /// session has stopped reporting a status and the in-place reload has
    /// actually fired — drives the context menu's "Reloading..." collapse
    /// (FR-7.5).</summary>
    public bool IsReloadInProgress => _reloadRequested;

    public TrayIconController(
        TaskbarIcon taskbarIcon,
        SessionStateStore stateStore,
        IconImageCache iconCache,
        string appName,
        LagThresholds lagThresholds,
        IReadOnlyList<string> sessionNames,
        NotificationQueue notificationQueue,
        Action onSelfRestartNeeded,
        Action onReloadReady,
        ILogger<TrayIconController> logger)
    {
        _taskbarIcon = taskbarIcon;
        _stateStore = stateStore;
        _iconCache = iconCache;
        _appName = appName;
        _lagThresholds = lagThresholds;
        _sessionNames = sessionNames;
        _notificationQueue = notificationQueue;
        _onSelfRestartNeeded = onSelfRestartNeeded;
        _onReloadReady = onReloadReady;
        _logger = logger;
        Interlocked.CompareExchange(ref _processFirstControllerTimestamp, Stopwatch.GetTimestamp(), 0);
        _nextStartupRecreateAt = ElapsedSinceProcessFirstController() + StartupRecreateInterval;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Requests an in-place config reload —
    /// the caller is expected to have already disabled monitoring (so every
    /// session gets terminated on the next poll); this just arms the check
    /// that fires the actual reload once they're all confirmed
    /// stopped (FR-7.1).</summary>
    public void RequestReload()
    {
        _logger.LogInformation("Reload requested; waiting for every session to stop before reloading");
        _reloadRequested = true;
    }

    public void Start()
    {
        _logger.LogInformation("Tray icon controller started (1s tick)");
        _taskbarIcon.PreviewTrayContextMenuOpen += OnPreviewTrayContextMenuOpen;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _logger.LogInformation("Tray icon controller stopped");
        _taskbarIcon.PreviewTrayContextMenuOpen -= OnPreviewTrayContextMenuOpen;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Stop();
    }

    /// <summary>H.NotifyIcon calls TaskbarIcon.ShowContextMenu(...) — which has
    /// no try/catch — synchronously from its own native WndProc callback
    /// (visible in a crash's call stack as a "Native to Managed Transition").
    /// .NET always fail-fasts the whole process on an exception escaping a
    /// callback invoked by native code, before any managed exception handler
    /// (Dispatcher filter, DispatcherUnhandledException, even
    /// AppDomain.UnhandledException) gets a chance to run — so an exception
    /// there (e.g. a XamlParseException the first time the ContextMenu's
    /// template is realized) was previously silent: no log, no message box,
    /// just the process disappearing.
    ///
    /// This handler cancels that synchronous, native-callback attempt
    /// (Handled = true, per ShowContextMenu's own early-return check) and
    /// re-issues it via Dispatcher.BeginInvoke instead — a normal queued
    /// dispatcher operation. If it throws there, it's an ordinary managed-only
    /// call stack, so the existing Dispatcher/AppDomain handlers in
    /// App.xaml.cs catch and log it like any other UI exception. The
    /// _isReopeningContextMenu guard lets that second, deferred call actually
    /// open the menu instead of cancelling itself again.</summary>
    private void OnPreviewTrayContextMenuOpen(object sender, RoutedEventArgs e)
    {
        if (_isReopeningContextMenu)
            return;

        e.Handled = true;
        var cursorPosition = GetCursorScreenPosition();
        _taskbarIcon.Dispatcher.BeginInvoke(() =>
        {
            _isReopeningContextMenu = true;
            try
            {
                _taskbarIcon.ShowContextMenu(cursorPosition);
            }
            finally
            {
                _isReopeningContextMenu = false;
            }
        });
    }

    private static System.Drawing.Point GetCursorScreenPosition()
    {
        GetCursorPos(out var point);
        return new System.Drawing.Point(point.X, point.Y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    // Shell_NotifyIconGetRect: HRESULT, 0 (S_OK) when the icon exists in the
    // notification area (or its overflow chevron) and the rectangle is valid.
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

    // NOTIFYICONIDENTIFIER. H.NotifyIcon registers its icon with a GUID, so
    // the GUID identifies it (the shell then ignores window handle and id).
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint IconId;
        public Guid IconGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Sleep/hibernate pauses polling, so on resume the last
    /// successful poll is as old as the sleep itself — which would trip the
    /// Restart threshold at once. Staleness is therefore measured from the
    /// resume instant until the first fresh poll lands.</summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // The timer does not tick during sleep: the next tick gap is the
        // sleep itself. Flagged on Suspend too, because on wake the timer
        // can fire before the Resume event reaches this thread.
        if (e.Mode == PowerModes.Suspend)
            _isSuspended = true;

        if (e.Mode != PowerModes.Resume)
            return;

        _isSuspended = false;

        _resumedUtc = DateTimeOffset.UtcNow;
        _resumedSinceLastTick = true;
        // The shell may have dropped the native tray icon while the system
        // was down; recreating it is cheap, and the de-duplicated update
        // path would otherwise never notice (no update, no error).
        _recreateIconRequested = true;
        _logger.LogInformation("System resumed from sleep/hibernate; staleness is measured from now, tray icon will be recreated");
    }

    private void Tick()
    {
        var snapshot = _stateStore.Get();
        var now = DateTimeOffset.UtcNow;
        var tickTimestamp = Stopwatch.GetTimestamp();
        if (_lastTickTimestamp != 0 && !_resumedSinceLastTick && !_isSuspended)
        {
            var tickGap = Stopwatch.GetElapsedTime(_lastTickTimestamp, tickTimestamp);
            if (tickGap >= TickLagWarningThreshold)
                _logger.LogWarning("Tray icon tick delayed by {Lag:F1}s: the UI thread was blocked or starved", tickGap.TotalSeconds);
        }
        _resumedSinceLastTick = false;
        _lastTickTimestamp = tickTimestamp;
        var lastPollUtc = snapshot.LastSuccessfulPollUtc > _resumedUtc ? snapshot.LastSuccessfulPollUtc : _resumedUtc;

        if (!_restartTriggered && IsBeyondRestartThreshold(lastPollUtc, now, _lagThresholds))
        {
            _logger.LogWarning("Status stale past the Restart threshold; triggering self-restart");
            _restartTriggered = true;
            ShowPendingNotifications();
            Stop();
            _onSelfRestartNeeded();
            return;
        }

        if (_reloadRequested && AllSessionsStopped(snapshot.SessionStatuses, _sessionNames))
        {
            _logger.LogInformation("Every session has stopped; reloading config");
            _reloadRequested = false;
            ShowPendingNotifications();
            Stop();
            _onReloadReady();
            return;
        }

        var staleness = GetStalenessTier(lastPollUtc, now, _lagThresholds);
        var input = new TrayIconInput(snapshot.WorstCode, snapshot.Enabled, snapshot.ProfileJustUpdated, staleness);
        var state = TrayIconStateResolver.Resolve(input, _appName);

        Polled?.Invoke(snapshot, state);

        ScheduleStartupRecreate();

        var iconRecreated = _recreateIconRequested && TryRecreateNativeIcon();
        ShowPendingNotifications();

        if (!iconRecreated && _lastState is { } last && last.IconKey == state.IconKey && last.Tooltip == state.Tooltip)
            return;

        _logger.LogDebug("Tray icon state changed: {IconKey} — \"{Tooltip}\"", state.IconKey, state.Tooltip);

        try
        {
            if (iconRecreated || _lastState?.IconKey != state.IconKey)
            {
                if (_lastState?.IconKey != state.IconKey)
                    _logger.LogInformation("Tray icon changed: {PreviousIconKey} -> {IconKey}", _lastState?.IconKey, state.IconKey);
                _taskbarIcon.Icon = _iconCache.Get(state.IconKey);
            }

            _taskbarIcon.ToolTipText = state.Tooltip;
        }
        catch (InvalidOperationException ex)
        {
            // The native tray icon can go stale after the taskbar is torn
            // down (Explorer restart, or waking from sleep/hibernate)
            // without a TaskbarCreated message to recreate it — H.NotifyIcon
            // throws instead of no-op'ing, and retrying the update on the
            // same stale native icon can never succeed. The icon is
            // therefore recreated next tick, and _lastState is deliberately
            // left unset so the same update is re-applied afterwards, rather
            // than surfacing a disruptive error dialog every second.
            _recreateIconRequested = true;
            _logger.LogWarning(ex, "Failed to update the tray icon/tooltip; recreating the native icon next tick");
            return;
        }

        _lastState = state;
    }

    /// <summary>Drains and shows the queued notifications. Called after the
    /// native icon was possibly recreated (deleting the native icon can
    /// dismiss a balloon shown just before), and drained only here so that an
    /// exception earlier in the tick cannot lose already-drained
    /// messages.</summary>
    private void ShowPendingNotifications()
    {
        // Left queued while the native icon is missing (recreate pending or
        // failed): showing on it throws, and the messages are already drained.
        if (_recreateIconRequested || !_taskbarIcon.TrayIcon.IsCreated)
            return;

        var messages = _notificationQueue.DrainAll();
        for (var i = 0; i < messages.Count; i++)
        {
            try
            {
                _taskbarIcon.ShowNotification(messages[i].Title, messages[i].Body);
                _lastNotificationTimestamp = Stopwatch.GetTimestamp();
            }
            catch (InvalidOperationException ex)
            {
                // Never throws out of here: this runs ahead of the
                // self-restart and reload hand-offs. The messages not shown
                // yet go back to the queue for the next tick.
                _logger.LogWarning(ex, "Failed to show notification '{Title}'; native icon will be recreated and the remaining messages re-queued", messages[i].Title);
                _recreateIconRequested = true;
                for (var j = i; j < messages.Count; j++)
                    _notificationQueue.Enqueue(messages[j]);
                return;
            }
        }
    }

    /// <summary>Arms a native icon recreate on the next tick (used when the
    /// initial creation at startup failed because the shell was not
    /// ready).</summary>
    public void RequestRecreate() => _recreateIconRequested = true;

    private static TimeSpan ElapsedSinceProcessFirstController() =>
        Stopwatch.GetElapsedTime(Interlocked.Read(ref _processFirstControllerTimestamp));

    /// <summary>Arms a native icon recreate when the startup schedule says
    /// so. Pure function of the monotonic elapsed time; never stacks on a
    /// recreate that is still pending (e.g. failing and retried every
    /// tick).</summary>
    private void ScheduleStartupRecreate()
    {
        var elapsed = ElapsedSinceProcessFirstController();
        if (elapsed >= StartupRecreateWindow)
        {
            if (!_startupWindowEndLogged && _startupRecreatesScheduled > 0)
            {
                _startupWindowEndLogged = true;
                _logger.LogInformation(
                    "Startup tray icon recreation window ended after {Count} scheduled recreate(s){Early}",
                    _startupRecreatesScheduled, _startupRecreationConfirmed ? " (stopped early after visibility confirmation)" : "");
            }
            return;
        }

        if (_startupRecreationConfirmed || _recreateIconRequested || elapsed < _nextStartupRecreateAt)
            return;

        if (_lastNotificationTimestamp != 0 && Stopwatch.GetElapsedTime(_lastNotificationTimestamp) < NotificationGracePeriod)
            return;

        _startupRecreatesScheduled++;
        _recreateIconRequested = true;
        _logger.LogInformation("Startup tray icon recreate attempt #{Count} ({Elapsed:F0}s after startup)", _startupRecreatesScheduled, elapsed.TotalSeconds);
    }

    private bool IsStartupScheduleActive() =>
        !_startupRecreationConfirmed && ElapsedSinceProcessFirstController() < StartupRecreateWindow;

    /// <summary>Asks the shell where the native icon is, and logs the answer.
    /// While the startup schedule is active it counts consecutive positive
    /// answers; once <see cref="RequiredVisibilityConfirmations"/> is reached
    /// the startup recreations stop (the icon is demonstrably registered in
    /// the notification area). A negative or failed check resets the count
    /// and the periodic recreation goes on. Once the schedule is over (after
    /// confirmation or after the window) the result is only logged,
    /// neutrally. Any recreate during the window counts, whatever triggered it
    /// (schedule, resume, failed update). The call is a synchronous round trip
    /// to Explorer on the UI thread, like Shell_NotifyIcon, hence the logged
    /// duration.</summary>
    private void CheckNativeIconVisibility(bool startupScheduleActive)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var trayIcon = _taskbarIcon.TrayIcon;
            var identifier = new NotifyIconIdentifier
            {
                Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                WindowHandle = trayIcon.WindowHandle,
                IconId = 0,
                IconGuid = trayIcon.Id,
            };
            var result = Shell_NotifyIconGetRect(ref identifier, out var rect);
            var isVisible = result == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top;

            if (!startupScheduleActive)
            {
                _logger.LogInformation(
                    "Tray icon visibility check after recreate: {Visibility} (HRESULT 0x{Result:X8}, {ElapsedMs} ms)",
                    isVisible ? "visible" : "NOT visible", result, stopwatch.ElapsedMilliseconds);
                return;
            }

            if (!isVisible)
            {
                _consecutiveVisibilityConfirmations = 0;
                _logger.LogInformation(
                    "Tray icon visibility check: NOT visible yet (HRESULT 0x{Result:X8}, {ElapsedMs} ms); recreations continue",
                    result, stopwatch.ElapsedMilliseconds);
                return;
            }

            _consecutiveVisibilityConfirmations++;
            _logger.LogInformation(
                "Tray icon visibility check: visible at ({Left},{Top})-({Right},{Bottom}), confirmation {Count}/{Required} ({ElapsedMs} ms)",
                rect.Left, rect.Top, rect.Right, rect.Bottom, _consecutiveVisibilityConfirmations, RequiredVisibilityConfirmations, stopwatch.ElapsedMilliseconds);

            if (_consecutiveVisibilityConfirmations >= RequiredVisibilityConfirmations)
            {
                _startupRecreationConfirmed = true;
                _logger.LogInformation("Tray icon confirmed visible {Required} times in a row; startup recreations stop", RequiredVisibilityConfirmations);
            }
        }
        catch (Exception ex)
        {
            // Diagnostic only: an unavailable API must not break the tick.
            _consecutiveVisibilityConfirmations = 0;
            _logger.LogWarning(ex, "Tray icon visibility check failed after {ElapsedMs} ms; recreations continue", stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>Plans the next startup recreate one fixed interval after
    /// this one finished (never replaying the ones missed while the UI
    /// thread was blocked). The interval is deliberately constant, with no
    /// backoff.</summary>
    private void OnStartupRecreateDone()
    {
        _nextStartupRecreateAt = ElapsedSinceProcessFirstController() + StartupRecreateInterval;
    }

    /// <summary>Drops the (possibly stale) native tray icon and adds a fresh
    /// one. Returns false — leaving the request armed for the next tick — if
    /// the shell isn't ready yet (typical right after a resume). The caller
    /// must re-apply the icon and tooltip after a successful recreate.</summary>
    private bool TryRecreateNativeIcon()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _taskbarIcon.TrayIcon.TryRemove();
            // See App.OnStartup: the default would put the whole process in
            // Efficiency Mode (IDLE priority) at every recreate.
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
            _recreateIconRequested = false;
            _logger.LogInformation("Native tray icon recreated in {ElapsedMs} ms (retry count: {RetryCount})", stopwatch.ElapsedMilliseconds, _recreateRetryCount);
            _recreateRetryCount = 0;
            // Any successful recreate satisfies the current schedule slot.
            OnStartupRecreateDone();
            CheckNativeIconVisibility(IsStartupScheduleActive());
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _recreateRetryCount++;
            _consecutiveVisibilityConfirmations = 0;
            _logger.LogWarning(ex, "Failed to recreate the native tray icon after {ElapsedMs} ms (retry count: {RetryCount}); will retry next tick", stopwatch.ElapsedMilliseconds, _recreateRetryCount);
            return false;
        }
    }

    /// <summary>Implements the staleness checks (FR-6.1/6.2/6.3) as
    /// pure functions of (last successful poll, now, thresholds).</summary>
    private static StalenessTier GetStalenessTier(DateTimeOffset lastSuccessfulPollUtc, DateTimeOffset nowUtc, LagThresholds thresholds)
    {
        var age = nowUtc - lastSuccessfulPollUtc;
        if (age > thresholds.Error)
            return StalenessTier.Error;
        if (age > thresholds.Warning)
            return StalenessTier.Warning;
        if (age > thresholds.Info)
            return StalenessTier.Info;
        return StalenessTier.None;
    }

    private static bool IsBeyondRestartThreshold(DateTimeOffset lastSuccessfulPollUtc, DateTimeOffset nowUtc, LagThresholds thresholds)
        => (nowUtc - lastSuccessfulPollUtc) > thresholds.Restart;

    /// <summary>After a user-requested "Reload config & restart mutagen"
    /// (FR-7.1), the app waits until every configured session has actually
    /// stopped reporting a status before spawning the replacement process,
    /// so the restart doesn't race a still-running `mutagen`
    /// session.</summary>
    private static bool AllSessionsStopped(
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses, IReadOnlyCollection<string> sessionNames)
    {
        foreach (var name in sessionNames)
            if (statuses.TryGetValue(name, out var status) && status is not null && !string.IsNullOrEmpty(status.Status))
                return false;

        return true;
    }
}

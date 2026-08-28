using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
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
/// <see cref="ISessionStateStore"/>.
/// </summary>
public sealed class TrayIconController
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly ISessionStateStore _stateStore;
    private readonly IconImageCache _iconCache;
    private readonly string _appName;
    private readonly LagThresholds _lagThresholds;
    private readonly IReadOnlyList<string> _sessionNames;
    private readonly INotificationQueue _notificationQueue;
    private readonly Action _onSelfRestartNeeded;
    private readonly ILogger<TrayIconController> _logger;
    private readonly DispatcherTimer _timer;
    private TrayIconState? _lastState;
    private bool _restartTriggered;
    private bool _restartRequested;
    private bool _isReopeningContextMenu;

    /// <summary>True from <see cref="RequestRestart"/> until every configured
    /// session has stopped reporting a status and the self-restart has
    /// actually fired — drives the context menu's "Restarting..." collapse
    /// (FR-7.5).</summary>
    public bool IsRestartInProgress => _restartRequested;

    public TrayIconController(
        TaskbarIcon taskbarIcon,
        ISessionStateStore stateStore,
        IconImageCache iconCache,
        string appName,
        LagThresholds lagThresholds,
        IReadOnlyList<string> sessionNames,
        INotificationQueue notificationQueue,
        Action onSelfRestartNeeded,
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
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Requests a monitored restart —
    /// the caller is expected to have already disabled monitoring (so every
    /// session gets terminated on the next poll); this just arms the check
    /// that fires the actual self-restart once they're all confirmed
    /// stopped (FR-7.1).</summary>
    public void RequestRestart()
    {
        _logger.LogInformation("Restart requested; waiting for every session to stop before restarting");
        _restartRequested = true;
    }

    public void Start()
    {
        _logger.LogInformation("Tray icon controller started (1s tick)");
        _taskbarIcon.PreviewTrayContextMenuOpen += OnPreviewTrayContextMenuOpen;
        Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _logger.LogInformation("Tray icon controller stopped");
        _taskbarIcon.PreviewTrayContextMenuOpen -= OnPreviewTrayContextMenuOpen;
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
        {
            return;
        }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void Tick()
    {
        foreach (var message in _notificationQueue.DrainAll())
        {
            _taskbarIcon.ShowNotification(message.Title, message.Body);
        }

        var snapshot = _stateStore.Get();
        var now = DateTimeOffset.UtcNow;

        if (!_restartTriggered && StalenessCalculator.IsBeyondRestartThreshold(snapshot.LastSuccessfulPollUtc, now, _lagThresholds))
        {
            _logger.LogWarning("Status stale past the Restart threshold; triggering self-restart");
            _restartTriggered = true;
            Stop();
            _onSelfRestartNeeded();
            return;
        }

        if (_restartRequested && RestartReadiness.AllSessionsStopped(snapshot.SessionStatuses, _sessionNames))
        {
            _logger.LogInformation("Every session has stopped; restarting");
            _restartRequested = false;
            Stop();
            _onSelfRestartNeeded();
            return;
        }

        var staleness = StalenessCalculator.GetTier(snapshot.LastSuccessfulPollUtc, now, _lagThresholds);
        var input = new TrayIconInput(snapshot.WorstCode, snapshot.Enabled, snapshot.ProfileJustUpdated, staleness);
        var state = TrayIconStateResolver.Resolve(input, _appName);

        if (_lastState is { } last && last.IconKey == state.IconKey && last.Tooltip == state.Tooltip)
        {
            return;
        }

        _logger.LogDebug("Tray icon state changed: {IconKey} — \"{Tooltip}\"", state.IconKey, state.Tooltip);

        if (_lastState?.IconKey != state.IconKey)
        {
            _logger.LogInformation("Tray icon changed: {PreviousIconKey} -> {IconKey}", _lastState?.IconKey, state.IconKey);
            _taskbarIcon.Icon = _iconCache.Get(state.IconKey);
        }

        _taskbarIcon.ToolTipText = state.Tooltip;
        _lastState = state;
    }
}

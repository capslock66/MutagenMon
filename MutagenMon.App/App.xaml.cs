using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Notifications;
using MutagenMon.Core.ProfileWatch;
using MutagenMon.Core.Resolution;
using MutagenMon.Core.Sessions;
using MutagenMon.Core.Status;

namespace MutagenMon.App;

/// <summary>
/// Composition root. No main window is shown at startup (ShutdownMode is
/// OnExplicitShutdown in App.xaml) — matches the legacy's hidden wx.Frame /
/// tray-icon-only model (NFR-7). Wires up: config + session loading, the
/// generic host (SessionMonitorService as a hosted background service), and
/// the tray icon (TrayIconController) on top of it.
///
/// Logging goes through <see cref="FileLoggerProvider"/> — a small
/// hand-rolled <c>ILoggerProvider</c>, no third-party logging library —
/// capturing one primary file, whose path is only known once
/// <see cref="MutagenMonOptions"/> is loaded (<see cref="FileLoggerProvider.SetPrimaryLogPath"/>);
/// deliberately no default path under the app's own directory before that,
/// and no fallback file next to the executable either — see
/// <see cref="FileLoggerProvider"/>'s remarks. Every Critical entry (which
/// includes a startup failure, even config loading itself failing, before
/// LogPath is even known) instead reaches the Windows Application Event
/// Log, a durable sink that doesn't depend on any path this app resolves.
/// </summary>
public partial class App : Application
{
    private FileLoggerProvider? _loggerProvider;
    private ILogger<App>? _logger;
    private IHost? _host;
    private TrayIconController? _trayIconController;
    private IconImageCache? _iconCache;
    private StatusWindow? _statusWindow;
    private SessionMonitorService? _monitorService;
    private ISessionStateStore? _stateStore;
    private ConflictResolutionService? _conflictResolutionService;
    private ILogger<ConflictResolutionController>? _conflictResolutionControllerLogger;
    private IReadOnlyList<string> _sessionNames = Array.Empty<string>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var baseDir = AppContext.BaseDirectory;
        _loggerProvider = new FileLoggerProvider();
        _logger = new LoggerFactory(new[] { _loggerProvider }).CreateLogger<App>();

        // UnhandledExceptionFilter fires before WPF decides whether an
        // exception is "catchable", including for exceptions raised inside a
        // nested dispatcher frame (Dispatcher.PushFrame) — which is exactly
        // what opening a Popup/ContextMenu does. DispatcherUnhandledException
        // alone can miss those (a known WPF gotcha), which is why a
        // tray-icon context-menu exception could previously go completely
        // unlogged. Logging here first guarantees it never does, regardless
        // of whether WPF then treats it as catchable.
        Dispatcher.UnhandledExceptionFilter += OnDispatcherUnhandledExceptionFilter;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _logger.LogInformation("MutagenMon starting. BaseDirectory={BaseDir}", baseDir);

        try
        {
            // Load config first, still ahead of everything else — so
            // LogPath/MinLogLevel (both config-driven) are in effect for as
            // much of startup as possible. It can't be moved any earlier
            // than the top of this try: a config-load failure here still
            // needs to land in the catch block below for the dedicated
            // "MutagenMon failed to start" dialog/shutdown (FR-14.1,
            // UT-14.2) — moving it before the try, ahead of the "MutagenMon
            // starting" line above, would route a config failure through
            // the generic OnDispatcherUnhandledException path instead,
            // which (deliberately, see that handler) does NOT shut down —
            // leaving a half-initialized app with no tray icon and no way
            // for the user to interact with it, worse than today's clean
            // exit.
            var configPath = Path.Combine(baseDir, "config", "config_mutagenmon.json");
            _logger.LogInformation("Loading configuration from {ConfigPath}", configPath);
            var options = ConfigLoader.Load(configPath);
            _loggerProvider.SetPrimaryLogPath(ResolveLogFilePath(baseDir, options.LogPath));
            _loggerProvider.SetMinLevel(options.MinLogLevel);
            _logger.LogInformation(
                "Configuration loaded: PollPeriod={PollPeriodMs}ms, StartEnabled={StartEnabled}, LogPath={LogPath}",
                options.MutagenPollPeriodMs, options.StartEnabled, options.LogPath);

            // Show the tray icon before the rest of startup (session
            // loading, DI container build, host start below all take real
            // time). With no main window, nothing else makes the app visible
            // in the meantime, so the user would otherwise stare at what
            // looks like a failed launch. TIC-3's "waiting for status"
            // (lightgray-init) state is exactly the right placeholder here —
            // it already means "no poll result yet", which is true at this
            // point by construction.
            var iconCache = new IconImageCache(Path.Combine(baseDir, "Assets", "Icons"));
            _iconCache = iconCache;
            var trayIcon = (TaskbarIcon)Resources["TrayIcon"];
            // With no main window, TaskbarIcon's native icon is never created
            // implicitly (it normally happens on Loaded, when a control enters
            // a live visual tree — which never happens for a resource that is
            // only ever referenced from code). ForceCreate() is the pattern
            // H.NotifyIcon's own "windowless" sample app uses for exactly this
            // case; without it, the app runs with no visible tray icon at all.
            trayIcon.ForceCreate();
            trayIcon.Icon = iconCache.Get("lightgray-init");
            _logger.LogInformation("Tray icon shown early (lightgray-init, waiting for status)");

            var sessionsPath = Path.Combine(baseDir, options.MutagenSessionsBatFile.Replace('/', Path.DirectorySeparatorChar));
            _logger.LogInformation("Loading session definitions from {SessionsPath}", sessionsPath);
            var sessionResult = SessionDefinitionLoader.ParseFile(sessionsPath);
            _logger.LogInformation("Loaded {SessionCount} session definition(s): {SessionNames}",
                sessionResult.Sessions.Count, string.Join(", ", sessionResult.Sessions.Select(s => s.Name)));
            foreach (var duplicate in sessionResult.DuplicateNames)
                _logger.LogWarning("Duplicate session name in {File}: {Name}", sessionsPath, duplicate);

            _logger.LogInformation("Building application host");
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(_loggerProvider);
            builder.Services.AddSingleton(Options.Create(options));
            builder.Services.AddSingleton<IReadOnlyList<SessionDefinition>>(sessionResult.Sessions);
            builder.Services.AddSingleton<IMutagenCliClient, MutagenCliClient>();
            builder.Services.AddSingleton<ISessionStateStore, SessionStateStore>();
            builder.Services.AddSingleton<IFileTimestampProvider, FileTimestampProvider>();
            builder.Services.AddSingleton<INotificationQueue, NotificationQueue>();
            builder.Services.AddSingleton(new RestartLogWriter(ResolveLogDirectory(baseDir, options.LogPath)));
            builder.Services.AddSingleton<SessionMonitorService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionMonitorService>());
            builder.Services.AddSingleton<IConflictFileClient, ConflictFileClient>();
            builder.Services.AddSingleton(new ResolveLogWriter(ResolveLogDirectory(baseDir, options.LogPath)));
            builder.Services.AddSingleton<ConflictResolutionService>();

            _host = builder.Build();
            _logger.LogInformation("Starting background session monitor");
            await _host.StartAsync();
            _logger.LogInformation("Background session monitor started");

            _sessionNames = sessionResult.Sessions.Select(s => s.Name).ToArray();
            _monitorService = _host.Services.GetRequiredService<SessionMonitorService>();
            var stateStore = _host.Services.GetRequiredService<ISessionStateStore>();
            _stateStore = stateStore;
            _conflictResolutionService = _host.Services.GetRequiredService<ConflictResolutionService>();
            _conflictResolutionControllerLogger = _host.Services.GetRequiredService<ILogger<ConflictResolutionController>>();
            var trayIconLogger = _host.Services.GetRequiredService<ILogger<TrayIconController>>();
            var notificationQueue = _host.Services.GetRequiredService<INotificationQueue>();

            _trayIconController = new TrayIconController(
                trayIcon, stateStore, iconCache, options.TrayTooltip, options.StatusMaxLag.ToLagThresholds(),
                _sessionNames, notificationQueue, OnSelfRestartNeeded, trayIconLogger);
            _trayIconController.Polled += OnPolled;
            _trayIconController.Start();
            _logger.LogInformation("MutagenMon startup complete — tray icon is live");
        }
        catch (Exception ex)
        {
            // Critical-level entries always reach the Windows Event Log
            // (see FileLoggerProvider.Write) — no separate call needed here,
            // even though config (and therefore LogPath) never loaded.
            _logger.LogCritical(ex, "MutagenMon failed to start");
            MessageBox.Show(
                $"MutagenMon failed to start:\n\n{ex}",
                "MutagenMon — startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void OnSelfRestartNeeded()
    {
        _logger?.LogWarning("Status has been stale past the Restart threshold; restarting");
        SelfRestart.RestartAndExit();
    }

    private void OnShowStatusClick(object sender, RoutedEventArgs e)
    {
        _logger?.LogDebug("Show status clicked");
        if (_statusWindow is null)
        {
            _statusWindow = new StatusWindow();
            _statusWindow.ResolveConflictsRequested += OnResolveConflictsRequested;
        }
        if (_stateStore is not null && _trayIconController?.CurrentState is { } state)
            _statusWindow.UpdateContent(state.Tooltip, _iconCache?.GetImageSource(state.IconKey), _stateStore.Get(), _sessionNames);
        _statusWindow.Show();
        _statusWindow.Activate();
    }

    /// <summary>Keeps an already-open status view live (FR-8.4): every 1s
    /// tray-icon tick (<see cref="TrayIconController.Polled"/>) re-renders it
    /// with the latest snapshot. Re-assigning identical WPF property values
    /// (Text/Visibility) is a no-op internally, so this doesn't flicker or
    /// disturb the view when nothing actually changed.</summary>
    private void OnPolled(MonitorSnapshot snapshot, TrayIconState state)
    {
        if (_statusWindow is { IsVisible: true })
            _statusWindow.UpdateContent(state.Tooltip, _iconCache?.GetImageSource(state.IconKey), snapshot, _sessionNames);
    }

    /// <summary>Handles the status view's "Resolve conflicts" action (FR-8.2 ->
    /// FR-9). Composes a fresh <see cref="ConflictResolutionController"/> per
    /// invocation — no state to keep between runs.</summary>
    private async void OnResolveConflictsRequested(object? sender, EventArgs e)
    {
        if (_stateStore is null || _conflictResolutionService is null || _conflictResolutionControllerLogger is null || _statusWindow is null)
            return;

        _logger?.LogInformation("Resolve conflicts requested");
        var controller = new ConflictResolutionController(
            _statusWindow, _stateStore, _sessionNames, _conflictResolutionService, _conflictResolutionControllerLogger);
        await controller.RunAsync();
    }

    /// <summary>Handles the "Reload config & restart mutagen" action (FR-7.1):
    /// disable monitoring (terminating every session on the next poll) and
    /// arm the tray icon controller's restart-readiness check.</summary>
    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation("Reload config & restart mutagen requested");
        _monitorService?.SetEnabled(false);
        _trayIconController?.RequestRestart();
    }

    /// <summary>Handles the enable/disable monitoring toggle (FR-7.2).</summary>
    private void OnToggleMonitoringClick(object sender, RoutedEventArgs e)
    {
        if (_monitorService is null)
            return;
        var newEnabled = !_monitorService.IsEnabled;
        _logger?.LogInformation("Toggling monitoring to {Enabled}", newEnabled);
        _monitorService.SetEnabled(newEnabled);
    }

    /// <summary>Implements the tray context menu's dynamic state
    /// (FR-7.2/FR-7.5) — refreshes the toggle item's label and collapses
    /// everything but "Restarting.../Exit" while a restart is in progress,
    /// right before the menu is actually shown. Items are addressed by
    /// position (matching the fixed order in App.xaml) rather than by name:
    /// x:Name on elements nested inside Application.Resources is not
    /// connected to a code-behind field the way it would be for a Window.</summary>
    private void OnTrayContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        var reloadItem = (MenuItem)menu.Items[0];
        var toggleItem = (MenuItem)menu.Items[1];
        var topSeparator = (UIElement)menu.Items[2];
        var showStatusItem = (MenuItem)menu.Items[3];
        var bottomSeparator = (UIElement)menu.Items[4];
        var restartingItem = (MenuItem)menu.Items[5];

        var restarting = _trayIconController?.IsRestartInProgress ?? false;

        reloadItem.Visibility = restarting ? Visibility.Collapsed : Visibility.Visible;
        toggleItem.Visibility = restarting ? Visibility.Collapsed : Visibility.Visible;
        topSeparator.Visibility = restarting ? Visibility.Collapsed : Visibility.Visible;
        showStatusItem.Visibility = restarting ? Visibility.Collapsed : Visibility.Visible;
        bottomSeparator.Visibility = restarting ? Visibility.Collapsed : Visibility.Visible;
        restartingItem.Visibility = restarting ? Visibility.Visible : Visibility.Collapsed;

        if (!restarting && _monitorService is not null)
            toggleItem.Header = _monitorService.IsEnabled ? "Stop Mutagen sessions" : "Start Mutagen sessions";
    }

    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation("Exit requested; shutting down");
        _trayIconController?.Stop();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        Shutdown();
    }

    private void OnDispatcherUnhandledExceptionFilter(object sender, DispatcherUnhandledExceptionFilterEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled exception caught by UnhandledExceptionFilter (possibly a nested dispatcher frame, e.g. a Popup/ContextMenu)");
        e.RequestCatch = true;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled exception on the UI thread");
        MessageBox.Show(
            $"MutagenMon hit an unexpected error:\n\n{e.Exception}",
            "MutagenMon — error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger?.LogCritical(ex, "Unhandled exception on a background thread");
        else
            _logger?.LogCritical("Unhandled exception on a background thread: {ExceptionObject}", e.ExceptionObject);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    /// <summary>Resolves logPath to a log directory (relative to baseDir
    /// unless logPath is itself rooted/absolute), creates it if needed, and
    /// returns the full path to mutagenMon.log inside it.</summary>
    private static string ResolveLogFilePath(string baseDir, string logPath) =>
        Path.Combine(ResolveLogDirectory(baseDir, logPath), "mutagenMon.log");

    /// <summary>Shared by the primary log (mutagenMon.log) and the dedicated
    /// resolve log (resolve.log, FR-9.7/FR-14.3) — same LogPath resolution
    /// rule (relative to baseDir unless rooted/absolute).</summary>
    private static string ResolveLogDirectory(string baseDir, string logPath)
    {
        var logDir = Path.IsPathRooted(logPath) ? logPath : Path.Combine(baseDir, logPath);
        Directory.CreateDirectory(logDir);
        return logDir;
    }
}

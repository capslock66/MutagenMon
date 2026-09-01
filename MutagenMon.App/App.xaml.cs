using System.Diagnostics;
using System.IO;
using System.Threading;
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
    private const string SingleInstanceMutexName = "MutagenMon-SingleInstance";
    private const string ShowStatusEventName = "MutagenMon-ShowStatus";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showStatusEvent;
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
    private MutagenMonOptions? _options;
    private INotificationQueue? _notificationQueue;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // NFR-3: single-instance enforcement. The mutex is created (not just
        // opened) atomically by the constructor, so createdNew tells us
        // unambiguously whether we're first. Checked before any other
        // startup work so a second launch exits as cheaply as possible.
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            FileLoggerProvider.WriteToWindowsEventLog(
                "MutagenMon is already running; asking the running instance to show its status window and exiting.",
                EventLogEntryType.Information);
            SignalRunningInstance();
            Shutdown();
            return;
        }

        // Created ahead of the thread that consumes it (below, once _logger
        // and _iconCache exist) so a second instance racing in during our
        // own startup can still signal us: EventWaitHandle latches a Set()
        // until the next WaitOne(), even if no thread is waiting yet.
        _showStatusEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowStatusEventName);

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
            _options = options;
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

            // _logger and _iconCache (both required by ShowStatusWindow) are
            // ready as of this point, so it's now safe to start reacting to
            // a second instance's signal.
            new Thread(WatchForShowStatusRequests) { IsBackground = true, Name = "MutagenMon-ShowStatusWatcher" }.Start();

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
            builder.Services.AddSingleton<SessionMonitorService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionMonitorService>());
            builder.Services.AddSingleton<IConflictFileClient, ConflictFileClient>();
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
            _notificationQueue = _host.Services.GetRequiredService<INotificationQueue>();

            _trayIconController = BuildAndStartTrayIconController(options, _sessionNames);
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

    /// <summary>Builds and starts a <see cref="TrayIconController"/> against
    /// the given options/session names, reusing everything that isn't
    /// options-dependent (the tray icon resource itself, <see cref="_stateStore"/>,
    /// <see cref="_iconCache"/>, <see cref="_notificationQueue"/>, and the
    /// host's logger factory). Used both at startup and by
    /// <see cref="OnReloadReady"/> to rebuild the controller after an
    /// in-place config reload (FR-7.1).</summary>
    private TrayIconController BuildAndStartTrayIconController(MutagenMonOptions options, IReadOnlyList<string> sessionNames)
    {
        var trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        var trayIconLogger = _host!.Services.GetRequiredService<ILogger<TrayIconController>>();
        var controller = new TrayIconController(
            trayIcon, _stateStore!, _iconCache!, options.TrayTooltip, options.StatusMaxLag.ToLagThresholds(),
            sessionNames, _notificationQueue!, OnSelfRestartNeeded, OnReloadReady, trayIconLogger);
        controller.Polled += OnPolled;
        controller.Start();
        return controller;
    }

    /// <summary>Implements the in-place half of FR-7.1: once
    /// <see cref="TrayIconController"/> confirms every session has stopped
    /// following a "Reload config" request, re-reads configuration and
    /// session definitions from disk and rebuilds the monitor/tray stack
    /// from them — without restarting the MutagenMon process itself (that
    /// remains reserved for the FR-6.3 staleness safety net, handled by
    /// <see cref="OnSelfRestartNeeded"/>). <see cref="SessionMonitorService"/>,
    /// <see cref="MutagenCliClient"/>, <see cref="ConflictFileClient"/>, and
    /// <see cref="ConflictResolutionService"/> all capture their
    /// options/session-derived state once in their constructors, so
    /// reconstructing them fresh is simpler and safer than adding live
    /// setters to each — <see cref="_stateStore"/>, <see cref="_notificationQueue"/>,
    /// <see cref="_iconCache"/>, the tray icon resource, and <see cref="_host"/>
    /// itself are the only things reused as-is.</summary>
    private async void OnReloadReady()
    {
        _logger?.LogInformation("Every configured session has stopped; reloading configuration in place");
        var baseDir = AppContext.BaseDirectory;
        MutagenMonOptions newOptions;
        SessionDefinitionLoadResult newSessionResult;
        try
        {
            var configPath = Path.Combine(baseDir, "config", "config_mutagenmon.json");
            newOptions = ConfigLoader.Load(configPath);
            var sessionsPath = Path.Combine(baseDir, newOptions.MutagenSessionsBatFile.Replace('/', Path.DirectorySeparatorChar));
            newSessionResult = SessionDefinitionLoader.ParseFile(sessionsPath);
            foreach (var duplicate in newSessionResult.DuplicateNames)
                _logger?.LogWarning("Duplicate session name in {File}: {Name}", sessionsPath, duplicate);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reload failed: could not load the new configuration; resuming with the previous configuration");
            MessageBox.Show(
                $"MutagenMon could not reload the configuration:\n\n{ex}\n\nThe previous configuration stays active.",
                "MutagenMon — reload error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _monitorService?.SetEnabled(true);
            _trayIconController = BuildAndStartTrayIconController(_options!, _sessionNames);
            return;
        }

        _logger?.LogInformation(
            "Configuration reloaded: PollPeriod={PollPeriodMs}ms, StartEnabled={StartEnabled}, LogPath={LogPath}",
            newOptions.MutagenPollPeriodMs, newOptions.StartEnabled, newOptions.LogPath);
        _loggerProvider!.SetPrimaryLogPath(ResolveLogFilePath(baseDir, newOptions.LogPath));
        _loggerProvider.SetMinLevel(newOptions.MinLogLevel);

        if (_monitorService is not null)
        {
            await _monitorService.StopAsync(CancellationToken.None);
            _monitorService.Dispose();
        }

        var newSessionNames = newSessionResult.Sessions.Select(s => s.Name).ToArray();
        var optionsWrapper = Options.Create(newOptions);
        IMutagenCliClient newCliClient = new MutagenCliClient(optionsWrapper, _host!.Services.GetRequiredService<ILogger<MutagenCliClient>>());
        IConflictFileClient newConflictFileClient = new ConflictFileClient(optionsWrapper, _host.Services.GetRequiredService<ILogger<ConflictFileClient>>());
        var newConflictResolutionService = new ConflictResolutionService(newConflictFileClient, _host.Services.GetRequiredService<ILogger<ConflictResolutionService>>());
        var newMonitorService = new SessionMonitorService(
            newCliClient, _stateStore!, optionsWrapper, newSessionResult.Sessions,
            _host.Services.GetRequiredService<IFileTimestampProvider>(),
            newConflictResolutionService, _notificationQueue!,
            _host.Services.GetRequiredService<ILogger<SessionMonitorService>>());
        await newMonitorService.StartAsync(CancellationToken.None);

        _options = newOptions;
        _sessionNames = newSessionNames;
        _monitorService = newMonitorService;
        _conflictResolutionService = newConflictResolutionService;
        _trayIconController = BuildAndStartTrayIconController(newOptions, newSessionNames);

        _logger?.LogInformation("Reload complete — monitoring resumed with the new configuration");
    }

    private void OnShowStatusClick(object sender, RoutedEventArgs e)
    {
        _logger?.LogDebug("User action: show status clicked");
        ShowStatusWindow();
    }

    private void ShowStatusWindow()
    {
        if (_statusWindow is null)
        {
            _statusWindow = new StatusWindow(_logger!, _iconCache!);
            _statusWindow.ResolveConflictsRequested += OnResolveConflictsRequested;
            _statusWindow.ReloadConfigRequested += OnStatusWindowReloadConfigRequested;
            _statusWindow.ToggleMonitoringRequested += OnStatusWindowToggleMonitoringRequested;
            _statusWindow.ExitRequested += OnStatusWindowExitRequested;
        }
        if (_stateStore is not null)
            _statusWindow.UpdateContent(_stateStore.Get(), _sessionNames, _trayIconController?.IsReloadInProgress ?? false);
        _statusWindow.Show();
        _statusWindow.Activate();
    }

    /// <summary>Second half of the single-instance flow (NFR-3): opens the
    /// first instance's <see cref="ShowStatusEventName"/> handle and signals
    /// it, so that instance shows its status window before this one exits.
    /// </summary>
    private static void SignalRunningInstance()
    {
        try
        {
            using var showStatusEvent = EventWaitHandle.OpenExisting(ShowStatusEventName);
            showStatusEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Startup race: the first instance hasn't created its event yet — nothing to signal.
        }
    }

    /// <summary>Runs for the lifetime of the (first, and only) instance:
    /// blocks on <see cref="_showStatusEvent"/> and shows the status window
    /// on the UI thread each time a second instance signals it.</summary>
    private void WatchForShowStatusRequests()
    {
        while (true)
        {
            _showStatusEvent!.WaitOne();
            Dispatcher.BeginInvoke(() =>
            {
                _logger?.LogInformation("Show-status request received from a second instance; showing status window");
                ShowStatusWindow();
            });
        }
    }

    /// <summary>Keeps an already-open status view live (FR-8.4): every 1s
    /// tray-icon tick (<see cref="TrayIconController.Polled"/>) re-renders it
    /// with the latest snapshot. Re-assigning identical WPF property values
    /// (Text/Visibility) is a no-op internally, so this doesn't flicker or
    /// disturb the view when nothing actually changed.</summary>
    private void OnPolled(MonitorSnapshot snapshot, TrayIconState state)
    {
        if (_statusWindow is { IsVisible: true })
            _statusWindow.UpdateContent(snapshot, _sessionNames, _trayIconController?.IsReloadInProgress ?? false);
    }

    /// <summary>Handles the status view's "Resolve conflicts" action (FR-8.2 ->
    /// FR-9). Composes a fresh <see cref="ConflictResolutionController"/> per
    /// invocation — no state to keep between runs.</summary>
    private async void OnResolveConflictsRequested(object? sender, EventArgs e)
    {
        if (_stateStore is null || _conflictResolutionService is null || _conflictResolutionControllerLogger is null || _statusWindow is null)
            return;

        var controller = new ConflictResolutionController(
            _statusWindow, _stateStore, _sessionNames, _conflictResolutionService, _conflictResolutionControllerLogger);
        await controller.RunAsync();
    }

    /// <summary>Handles the "Reload config & restart mutagen" action (FR-7.1):
    /// disable monitoring (terminating every session on the next poll) and
    /// arm the tray icon controller's reload-readiness check —
    /// <see cref="OnReloadReady"/> does the actual in-place reload once every
    /// session has stopped. Shared by the tray context menu and the status
    /// window's "Reload config" button.</summary>
    private void OnReloadClick(object sender, RoutedEventArgs e) => ReloadConfig();

    private void OnStatusWindowReloadConfigRequested(object? sender, EventArgs e) => ReloadConfig();

    private void ReloadConfig()
    {
        _logger?.LogInformation("User action: reload config & restart mutagen requested");
        _monitorService?.SetEnabled(false);
        _trayIconController?.RequestReload();
    }

    /// <summary>Handles the enable/disable monitoring toggle (FR-7.2). Shared
    /// by the tray context menu and the status window's "Stop/Start Mutagen
    /// sessions" button.</summary>
    private void OnToggleMonitoringClick(object sender, RoutedEventArgs e) => ToggleMonitoring();

    private void OnStatusWindowToggleMonitoringRequested(object? sender, EventArgs e) => ToggleMonitoring();

    private void ToggleMonitoring()
    {
        if (_monitorService is null)
            return;
        var newEnabled = !_monitorService.IsEnabled;
        _logger?.LogInformation("User action: toggling monitoring to {Enabled}", newEnabled);
        _monitorService.SetEnabled(newEnabled);
    }

    /// <summary>Implements the tray context menu's dynamic state
    /// (FR-7.2/FR-7.5) — refreshes the toggle item's label and collapses
    /// everything but "Reloading.../Exit" while a reload is in progress,
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
        var reloadingItem = (MenuItem)menu.Items[5];

        var reloading = _trayIconController?.IsReloadInProgress ?? false;

        reloadItem.Visibility = reloading ? Visibility.Collapsed : Visibility.Visible;
        toggleItem.Visibility = reloading ? Visibility.Collapsed : Visibility.Visible;
        topSeparator.Visibility = reloading ? Visibility.Collapsed : Visibility.Visible;
        showStatusItem.Visibility = reloading ? Visibility.Collapsed : Visibility.Visible;
        bottomSeparator.Visibility = reloading ? Visibility.Collapsed : Visibility.Visible;
        reloadingItem.Visibility = reloading ? Visibility.Visible : Visibility.Collapsed;

        if (!reloading && _monitorService is not null)
            toggleItem.Header = _monitorService.IsEnabled ? "Stop Mutagen sessions" : "Start Mutagen sessions";
    }

    private async void OnExitClick(object sender, RoutedEventArgs e) => await ExitAsync();

    private async void OnStatusWindowExitRequested(object? sender, EventArgs e) => await ExitAsync();

    private async Task ExitAsync()
    {
        // Shared by both entry points (tray menu "Exit MutagenMon" and the
        // status window's "Exit" button) — asked here, before either one
        // does anything irreversible, so a "No" leaves everything running
        // exactly as it was.
        if (MessageBox.Show(
                "Are you sure you want to exit MutagenMon? Background synchronization will stop.",
                "MutagenMon — confirm exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _logger?.LogInformation("User action: exit cancelled at confirmation");
            return;
        }

        _logger?.LogInformation("User action: exit requested; shutting down");
        _statusWindow?.Hide();
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

    private static string ResolveLogDirectory(string baseDir, string logPath)
    {
        var logDir = Path.IsPathRooted(logPath) ? logPath : Path.Combine(baseDir, logPath);
        Directory.CreateDirectory(logDir);
        return logDir;
    }
}

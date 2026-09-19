using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public partial class App //: Application
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

    /// <summary>Tracks the one open <see cref="SyncStatusDetailWindow"/>
    /// per session, keyed by session name (FR-28.9) — since that popup is
    /// non-modal (FR-28.5), re-clicking the eye icon for a session that
    /// already has a popup open must bring the existing one to front
    /// instead of stacking a duplicate.</summary>
    private readonly Dictionary<string, SyncStatusDetailWindow> _syncStatusWindows = new();
    private SessionMonitorService? _monitorService;
    private SessionStateStore? _stateStore;
    private ConflictResolutionService? _conflictResolutionService;
    private ILogger<ConflictResolutionController>? _conflictResolutionControllerLogger;
    private SessionEditingService? _sessionEditingService;
    private MutagenCliClient? _mutagenCliClient;
    private IReadOnlyList<SessionDefinition> _sessionDefinitions = Array.Empty<SessionDefinition>();
    private IReadOnlyList<string> _sessionNames = Array.Empty<string>();
    private MutagenMonOptions? _options;
    private NotificationQueue? _notificationQueue;

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
            var options = LoadConfig(configPath);
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
            builder.Services.AddSingleton<MutagenCliClient>();
            builder.Services.AddSingleton<SessionStateStore>();
            builder.Services.AddSingleton<FileTimestampProvider>();
            builder.Services.AddSingleton<NotificationQueue>();
            builder.Services.AddSingleton<SessionMonitorService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionMonitorService>());
            builder.Services.AddSingleton<ConflictFileClient>();
            builder.Services.AddSingleton<ConflictResolutionService>();
            builder.Services.AddSingleton(sp => new SessionEditingService(
                sp.GetRequiredService<MutagenCliClient>(), sessionsPath, sp.GetRequiredService<ILogger<SessionEditingService>>()));

            _host = builder.Build();
            _logger.LogInformation("Starting background session monitor");
            await _host.StartAsync();
            _logger.LogInformation("Background session monitor started");

            _sessionDefinitions = sessionResult.Sessions;
            _sessionNames = sessionResult.Sessions.Select(s => s.Name).ToArray();
            _monitorService = _host.Services.GetRequiredService<SessionMonitorService>();
            var stateStore = _host.Services.GetRequiredService<SessionStateStore>();
            _stateStore = stateStore;
            _conflictResolutionService = _host.Services.GetRequiredService<ConflictResolutionService>();
            _sessionEditingService = _host.Services.GetRequiredService<SessionEditingService>();
            _mutagenCliClient = _host.Services.GetRequiredService<MutagenCliClient>();
            _conflictResolutionControllerLogger = _host.Services.GetRequiredService<ILogger<ConflictResolutionController>>();
            _notificationQueue = _host.Services.GetRequiredService<NotificationQueue>();

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
        string sessionsPath;
        try
        {
            var configPath = Path.Combine(baseDir, "config", "config_mutagenmon.json");
            newOptions = LoadConfig(configPath);
            sessionsPath = Path.Combine(baseDir, newOptions.MutagenSessionsBatFile.Replace('/', Path.DirectorySeparatorChar));
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
        MutagenCliClient newCliClient = new MutagenCliClient(optionsWrapper, _host!.Services.GetRequiredService<ILogger<MutagenCliClient>>());
        ConflictFileClient newConflictFileClient = new ConflictFileClient(optionsWrapper, _host.Services.GetRequiredService<ILogger<ConflictFileClient>>());
        var newConflictResolutionService = new ConflictResolutionService(newConflictFileClient, _host.Services.GetRequiredService<ILogger<ConflictResolutionService>>());
        var newMonitorService = new SessionMonitorService(
            newCliClient, _stateStore!, optionsWrapper, newSessionResult.Sessions,
            _host.Services.GetRequiredService<FileTimestampProvider>(),
            newConflictResolutionService, _notificationQueue!,
            _host.Services.GetRequiredService<ILogger<SessionMonitorService>>());
        await newMonitorService.StartAsync(CancellationToken.None);

        _options = newOptions;
        _sessionDefinitions = newSessionResult.Sessions;
        _sessionNames = newSessionNames;
        _monitorService = newMonitorService;
        _conflictResolutionService = newConflictResolutionService;
        _sessionEditingService = new SessionEditingService(
            newCliClient, sessionsPath, _host.Services.GetRequiredService<ILogger<SessionEditingService>>());
        _mutagenCliClient = newCliClient;
        _trayIconController = BuildAndStartTrayIconController(newOptions, newSessionNames);

        _logger?.LogInformation("Reload complete — monitoring resumed with the new configuration");
    }

    private void OnShowStatusClick(object sender, RoutedEventArgs e)
    {
        _logger?.LogDebug("User action: show status clicked");
        ShowStatusWindow();
    }

    /// <summary>Handles the tray menu's "Move to main screen" (FR-29):
    /// repositions every window this process currently has open — the
    /// status view, any open sync status popups (FR-28), edit/conflict
    /// dialogs, etc. — onto the primary monitor's work area, centered and
    /// clamped to fit. Useful after a monitor is disconnected/reconfigured
    /// and a window is left stranded off-screen. <see cref="Application.Windows"/>
    /// includes hidden windows too (<see cref="StatusWindow"/> hides rather
    /// than closes, FR-8), which is harmless here — repositioning an
    /// invisible window has no visible effect but keeps it correctly placed
    /// for whenever it's shown again.</summary>
    private void OnMoveToMainScreenClick(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation("User action: tray menu Move to main screen clicked");
        foreach (Window window in Application.Current.Windows)
            MoveWindowToPrimaryScreen(window);
    }

    private static void MoveWindowToPrimaryScreen(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        var workArea = SystemParameters.WorkArea;
        var width = Math.Min(window.ActualWidth, workArea.Width);
        var height = Math.Min(window.ActualHeight, workArea.Height);

        window.Left = workArea.Left + (workArea.Width - width) / 2;
        window.Top = workArea.Top + (workArea.Height - height) / 2;
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
            _statusWindow.AddSessionRequested += OnAddSessionRequested;
            _statusWindow.ViewSyncStatusRequested += OnViewSyncStatusRequested;
            _statusWindow.EditSessionRequested += OnEditSessionRequested;
            _statusWindow.DeleteSessionRequested += OnDeleteSessionRequested;
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

    /// <summary>Handles the toolbar's "Add" action (FR-16.1 -&gt; FR-27.4).
    /// The window only actually closes once <c>SessionEditingService</c>'s
    /// live `sync create` call has actually succeeded (<see
    /// cref="SessionEditWindow.SaveRequested"/>'s remarks) — a failed
    /// creation (e.g. an invalid `--default-owner`) re-enables the form with
    /// everything the user typed still there, instead of silently discarding
    /// it. On success, reuses the existing "Reload config &amp; restart"
    /// pathway (<see cref="ReloadConfig"/>) to bring the new session into the
    /// live monitor/grid — <see cref="SessionMonitorService"/> has no API to
    /// add a single session to its already-running poll loop, so a full
    /// reload is the only way to pick it up without a deeper Core change.
    /// Heavier than FR-27.5's "immediately, without... a manual reload"
    /// wording strictly asks for (it restarts every other session too),
    /// called out as a known gap for this phase rather than silently
    /// accepted.</summary>
    private void OnAddSessionRequested(object? sender, EventArgs e)
    {
        if (_sessionEditingService is null || _statusWindow is null || _logger is null)
            return;

        var window = new SessionEditWindow(null, _sessionNames, _logger) { Owner = _statusWindow };
        window.SaveRequested += async (_, _) =>
        {
            window.SetBusy(true);
            try
            {
                await _sessionEditingService.AddAsync(window.Result!, CancellationToken.None);
                _logger.LogInformation("Session added: {Name}", window.Result!.Name);
                window.CompleteSave();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add session '{Name}'", window.Result!.Name);
                window.SetBusy(false);
                MessageBox.Show(
                    window, $"MutagenMon could not create the session:\n\n{ex.Message}",
                    "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        if (window.ShowDialog() == true)
            ReloadConfig();
    }

    /// <summary>Handles a row's View sync status icon (FR-28): runs
    /// `mutagen sync list -l &lt;name&gt;` and shows its raw, unparsed output
    /// in a non-modal <see cref="SyncStatusDetailWindow"/> (FR-28.5) that
    /// the status view stays fully usable alongside. A failure on this
    /// first call shows an error dialog and never opens the popup at all
    /// (FR-28.3); once open, <see cref="RefreshSyncStatusAsync"/> handles
    /// re-running the command instead (FR-28.6). If that session already
    /// has a popup open, brings it to front instead of opening a second one
    /// (FR-28.9) — restoring it first if it was minimized.</summary>
    private async void OnViewSyncStatusRequested(object? sender, string name)
    {
        if (_mutagenCliClient is null || _statusWindow is null || _logger is null)
            return;

        if (_syncStatusWindows.TryGetValue(name, out var existingWindow))
        {
            _logger.LogInformation("Sync status popup already open for '{Name}'; bringing it to front", name);
            if (existingWindow.WindowState == WindowState.Minimized)
                existingWindow.WindowState = WindowState.Normal;
            existingWindow.Activate();
            return;
        }

        string detail;
        try
        {
            detail = await _mutagenCliClient.GetSyncStatusDetailAsync(name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve sync status for session '{Name}'", name);
            MessageBox.Show(
                _statusWindow, $"MutagenMon could not retrieve sync status for session '{name}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var window = new SyncStatusDetailWindow(_logger, name) { Owner = _statusWindow };
        window.SetStatusText(detail);
        window.RefreshRequested += async (_, _) => await RefreshSyncStatusAsync(window, name);
        window.Closed += (_, _) => _syncStatusWindows.Remove(name);
        _syncStatusWindows[name] = window;
        window.Show();
    }

    /// <summary>Re-runs FR-28.2's command for an already-open sync status
    /// popup (FR-28.6). On failure, the popup keeps its last successful
    /// content — an error dialog is shown instead of clearing it, since a
    /// stale-but-real result is more useful than nothing (FR-28.7).</summary>
    private async Task RefreshSyncStatusAsync(SyncStatusDetailWindow window, string name)
    {
        if (_mutagenCliClient is null || _logger is null)
            return;

        try
        {
            var detail = await _mutagenCliClient.GetSyncStatusDetailAsync(name, CancellationToken.None);
            window.SetStatusText(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh sync status for session '{Name}'", name);
            MessageBox.Show(
                window, $"MutagenMon could not refresh sync status for session '{name}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Handles a row's Edit icon (FR-17.2 -&gt; FR-27.4). Parses the
    /// session's stored line (<see cref="SessionCommandLineParser"/>) to
    /// pre-populate the window, keyed by its current name — see
    /// <see cref="OnAddSessionRequested"/>'s remarks re: keeping the window
    /// open (with the user's input intact) until the recreate actually
    /// succeeds, and re: the reload-based refresh.</summary>
    private void OnEditSessionRequested(object? sender, string name)
    {
        if (_sessionEditingService is null || _statusWindow is null || _logger is null)
            return;

        var definition = _sessionDefinitions.FirstOrDefault(d => d.Name == name);
        if (definition is null)
        {
            _logger.LogWarning("Edit requested for unknown session '{Name}' (already removed?)", name);
            return;
        }

        var model = SessionCommandLineParser.Parse(definition.RawCreateCommand);
        var window = new SessionEditWindow(model, _sessionNames, _logger) { Owner = _statusWindow };
        window.SaveRequested += async (_, _) =>
        {
            window.SetBusy(true);
            try
            {
                await _sessionEditingService.EditAsync(window.OriginalName!, window.Result!, CancellationToken.None);
                _logger.LogInformation("Session edited: {OldName} -> {NewName}", window.OriginalName, window.Result!.Name);
                window.CompleteSave();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save changes to session '{Name}'", name);
                window.SetBusy(false);
                MessageBox.Show(
                    window, $"MutagenMon could not save changes to session '{name}':\n\n{ex.Message}",
                    "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        if (window.ShowDialog() == true)
            ReloadConfig();
    }

    /// <summary>Handles a row's Delete icon (FR-17.3/FR-17.4). If removing
    /// the line fails (FR-17.5), <see cref="ReloadConfig"/> is deliberately
    /// NOT called — the grid keeps showing the session exactly as it was,
    /// rather than risk it looking deleted when the file removal didn't
    /// actually happen.</summary>
    private async void OnDeleteSessionRequested(object? sender, string name)
    {
        if (_sessionEditingService is null || _statusWindow is null || _logger is null)
            return;

        if (!GenericMessageDialog.ShowConfirm(
                _statusWindow, _logger, "Mutagen delete session", $"Delete session {name} ?", okLabel: "Yes", cancelLabel: "No"))
            return;

        try
        {
            await _sessionEditingService.DeleteAsync(name, CancellationToken.None);
            _logger.LogInformation("Session deleted: {Name}", name);
            ReloadConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete session '{Name}'", name);
            MessageBox.Show(
                _statusWindow, $"MutagenMon could not delete session '{name}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Handles the "Reload config & restart mutagen" action (FR-7.1):
    /// disable monitoring (terminating every session on the next poll) and
    /// arm the tray icon controller's reload-readiness check —
    /// <see cref="OnReloadReady"/> does the actual in-place reload once every
    /// session has stopped. Only reachable from the status window's toolbar
    /// (FR-16.1/16.2) — no longer duplicated in the tray context menu.</summary>
    private void OnStatusWindowReloadConfigRequested(object? sender, EventArgs e) => ReloadConfig();

    private void ReloadConfig()
    {
        _logger?.LogInformation("User action: reload config & restart mutagen requested");
        _monitorService?.SetEnabled(false);
        _trayIconController?.RequestReload();
    }

    /// <summary>Handles the enable/disable monitoring toggle (FR-7.2). Only
    /// reachable from the status window's toolbar (FR-16.5) — no longer
    /// duplicated in the tray context menu.</summary>
    private void OnStatusWindowToggleMonitoringRequested(object? sender, EventArgs e) => ToggleMonitoring();

    private void ToggleMonitoring()
    {
        if (_monitorService is null)
            return;
        var newEnabled = !_monitorService.IsEnabled;
        _logger?.LogInformation("User action: toggling monitoring to {Enabled}", newEnabled);
        _monitorService.SetEnabled(newEnabled);
    }

    /// <summary>Implements the tray context menu's dynamic state (FR-7.5) —
    /// collapses everything but "Reloading.../Exit" while a reload is in
    /// progress, right before the menu is actually shown. Items are
    /// addressed by position (matching the fixed order in App.xaml) rather
    /// than by name: x:Name on elements nested inside Application.Resources
    /// is not connected to a code-behind field the way it would be for a
    /// Window.</summary>
    private void OnTrayContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        var showStatusItem       = menu.Items[0] as MenuItem;
        var moveToMainScreenItem = menu.Items[1] as MenuItem;
        var bottomSeparator      = menu.Items[2] as UIElement;
        var reloadingItem        = menu.Items[3] as MenuItem;

        var reloading = _trayIconController?.IsReloadInProgress ?? false;

        showStatusItem?.Visibility       = reloading ? Visibility.Collapsed : Visibility.Visible;
        moveToMainScreenItem?.Visibility = reloading ? Visibility.Collapsed : Visibility.Visible;
        bottomSeparator?.Visibility      = reloading ? Visibility.Collapsed : Visibility.Visible;
        reloadingItem?.Visibility        = reloading ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Deferred via Dispatcher.BeginInvoke so it runs after
    /// H.NotifyIcon's native context menu has actually finished closing,
    /// rather than from inside its Click callback.</summary>
    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(async () => await ExitAsync());

    private async void OnStatusWindowExitRequested(object? sender, EventArgs e) => await ExitAsync();

    private async Task ExitAsync()
    {
        // Shared by both entry points (tray menu "Exit MutagenMon" and the
        // status window's "Exit" button) — asked here, before either one
        // does anything irreversible, so a "No" leaves everything running
        // exactly as it was.
        //
        // The confirmation is always shown with a real owner window
        // (GenericMessageDialog, like every other dialog in the app), never
        // via a bare, ownerless MessageBox.Show: clicking "Exit" straight
        // from the tray icon (no status window ever opened, _statusWindow
        // still null) left the confirmation with no owner at all, and an
        // unowned dialog shown right as the tray's native context menu
        // finishes closing loses Windows' foreground activation to the
        // shell and closes itself almost instantly — logged as "cancelled"
        // even though the user never got to answer. A transient invisible
        // window, closed right after, gives the dialog a real owner when
        // the status window isn't open yet.
        Window? owner = _statusWindow;
        Window? tempOwner = null;
        if (owner is null)
        {
            // Placed on the primary screen's work area (not off-screen) even
            // though it's invisible (0x0, no chrome): GenericMessageDialog's
            // WindowStartupLocation="CenterScreen" centers relative to its
            // owner's monitor, so an off-screen owner sent the confirmation
            // to whatever monitor Windows resolved that position to instead
            // of the main screen.
            var workArea = SystemParameters.WorkArea;
            tempOwner = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = workArea.Left + workArea.Width / 2,
                Top = workArea.Top + workArea.Height / 2,
            };
            tempOwner.Show();
            owner = tempOwner;
        }

        bool confirmed;
        try
        {
            confirmed = GenericMessageDialog.ShowConfirm(
                owner,
                _logger!,
                "MutagenMon — confirm exit",
                "Are you sure you want to exit MutagenMon? \nBackground synchronization will continue.",
                okLabel: "Yes",
                cancelLabel: "No");
        }
        finally
        {
            tempOwner?.Close();
        }

        if (!confirmed)
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

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads the app's configuration. The shipped config file is
    /// JSON with whole-line '#' comments (never inline trailing ones),
    /// stripped before parsing.</summary>
    private static MutagenMonOptions LoadConfig(string path) => ParseConfigText(File.ReadAllText(path));

    private static MutagenMonOptions ParseConfigText(string rawTextWithComments)
    {
        var cleaned = StripConfigCommentLines(rawTextWithComments);
        var options = JsonSerializer.Deserialize<MutagenMonOptions>(cleaned, ConfigJsonOptions)
            ?? throw new InvalidDataException("Config file parsed to a null document.");

        // Explicit %USERPROFILE% expansion for
        // MutagenProfileDir; ExpandEnvironmentVariables is a no-op for text with
        // no %...% placeholders, so this is safe to always apply.
        options.MutagenProfileDir = Environment.ExpandEnvironmentVariables(options.MutagenProfileDir);

        return options;
    }

    private static string StripConfigCommentLines(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith('#'))
                continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}

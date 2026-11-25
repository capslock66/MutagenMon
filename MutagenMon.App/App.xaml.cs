using System.Diagnostics;
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
using MutagenMon.Core.Recovery;
using MutagenMon.Core.Resolution;
using MutagenMon.Core.Scripts;
using MutagenMon.Core.Sessions;
using MutagenMon.Core.Status;
using MutagenMon.Core.Updates;

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

    /// <summary>The assembly version (Major.Minor.Build.Revision), set at build time.</summary>
    public static string AppVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

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

    /// <summary>The recovery of a halted session (FR-59): used by the "Accept
    /// deletion" action (FR-60) and, through the monitor, by the automatic fix (FR-61).</summary>
    private HaltedRecoveryService? _haltedRecoveryService;
    private IReadOnlyList<SessionDefinition> _sessionDefinitions = Array.Empty<SessionDefinition>();

    /// <summary>The per-session `@halted-auto-fix` overrides read from the
    /// sessions file (FR-64): used to pre-select the Add/Edit window's
    /// "Recovery" combo (FR-65.1). A session with no entry inherits the global flag.</summary>
    private IReadOnlyDictionary<string, bool> _haltedAutoFixDirectives = new Dictionary<string, bool>();
    private IReadOnlyList<string> _sessionNames = Array.Empty<string>();
    private MutagenMonOptions? _options;
    private bool _startupIconCreateFailed;
    private NotificationQueue? _notificationQueue;

    /// <summary>FR-66.2: how often the deploy folder is checked for a newer build.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(2);
    private DispatcherTimer? _updateTimer;
    private DateTime _nextScheduledUpdateCheck;
    private UpdateCheckResult _updateResult = UpdateCheckResult.None;
    private DateTime? _lastNotifiedUpdateUtc;
    private bool _updateCheckRunning;
    private bool _updateCheckPending;
    private bool _reloadRebuildRunning;
    // Completed while no check (nor its queued re-run) is in progress; the manual check waits on it.
    private TaskCompletionSource _updateCheckIdle = CreateCompletedSource();
    private string? _lastUpdateFailureReason;

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

        _logger.LogInformation("MutagenMon starting. Version={Version}, ProcessId={ProcessId}, BaseDirectory={BaseDir}", AppVersion, Environment.ProcessId, baseDir);

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
            EnsureHaltedConfigKeys(configPath);
            EnsureUpdateConfigKeys(configPath);
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
            // Each step is timed: at logon on a saturated machine this block
            // once took ~6 minutes, and without these lines nothing says which
            // call is the slow one (the UI thread is blocked, so nothing else logs).
            var startupStopwatch = Stopwatch.StartNew();
            var iconCache = new IconImageCache(Path.Combine(baseDir, "Assets", "Icons"));
            _iconCache = iconCache;
            _logger.LogInformation("Startup step: icon cache created ({ElapsedMs} ms)", startupStopwatch.ElapsedMilliseconds);

            // _logger and _iconCache (both required by ShowStatusWindow) are
            // ready as of this point, so it's now safe to start reacting to
            // a second instance's signal.
            new Thread(WatchForShowStatusRequests) { IsBackground = true, Name = "MutagenMon-ShowStatusWatcher" }.Start();

            _logger.LogInformation("Startup step: instantiating the TrayIcon resource");
            startupStopwatch.Restart();
            var trayIcon = (TaskbarIcon)Resources["TrayIcon"];
            _logger.LogInformation("Startup step: TrayIcon resource instantiated ({ElapsedMs} ms)", startupStopwatch.ElapsedMilliseconds);
            // With no main window, TaskbarIcon's native icon is never created
            // implicitly (it normally happens on Loaded, when a control enters
            // a live visual tree — which never happens for a resource that is
            // only ever referenced from code). ForceCreate() is the pattern
            // H.NotifyIcon's own "windowless" sample app uses for exactly this
            // case; without it, the app runs with no visible tray icon at all.
            _logger.LogInformation("Startup step: creating the native tray icon (Shell_NotifyIcon waits for Explorer)");
            startupStopwatch.Restart();
            // enablesEfficiencyMode: false — H.NotifyIcon's default turns on
            // Efficiency Mode (IDLE priority class + EcoQoS) for the WHOLE
            // process, which starves the UI thread and the poller (and the
            // mutagen/ssh children inheriting the priority) on a machine that
            // is saturated at logon.
            try
            {
                trayIcon.ForceCreate(enablesEfficiencyMode: false);
                _logger.LogInformation("Startup step: native tray icon created ({ElapsedMs} ms)", startupStopwatch.ElapsedMilliseconds);
                startupStopwatch.Restart();
                trayIcon.Icon = iconCache.Get("lightgray-init");
                _logger.LogInformation("Tray icon shown early (lightgray-init, waiting for status; icon assigned in {ElapsedMs} ms)", startupStopwatch.ElapsedMilliseconds);
            }
            catch (InvalidOperationException ex)
            {
                // The shell is not ready (logon on a saturated machine): do not
                // abort the whole startup. The tray icon controller recreates
                // the native icon every 10 s during the first minutes.
                _startupIconCreateFailed = true;
                _logger.LogWarning(ex, "Startup step: native tray icon creation failed after {ElapsedMs} ms; it will be retried by the tray icon controller", startupStopwatch.ElapsedMilliseconds);
            }

            var sessionsPath = Path.Combine(baseDir, options.MutagenSessionsBatFile.Replace('/', Path.DirectorySeparatorChar));
            _logger.LogInformation("Loading session definitions from {SessionsPath}", sessionsPath);
            startupStopwatch.Restart();
            var sessionResult = SessionDefinitionLoader.ParseFile(sessionsPath);
            _logger.LogInformation("Loaded {SessionCount} session definition(s): {SessionNames} ({ElapsedMs} ms)",
                sessionResult.Sessions.Count, string.Join(", ", sessionResult.Sessions.Select(s => s.Name)), startupStopwatch.ElapsedMilliseconds);
            foreach (var duplicate in sessionResult.DuplicateNames)
                _logger.LogWarning("Duplicate session name in {File}: {Name}", sessionsPath, duplicate);
            ApplyHaltedAutoFixDirectives(sessionResult);

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
            builder.Services.AddSingleton<EndpointPurger>();
            builder.Services.AddSingleton<HaltedRecoveryService>();
            builder.Services.AddSingleton(sp => new SessionEditingService(
                sp.GetRequiredService<MutagenCliClient>(), sessionsPath, sp.GetRequiredService<ILogger<SessionEditingService>>()));

            startupStopwatch.Restart();
            _host = builder.Build();
            _logger.LogInformation("Application host built ({ElapsedMs} ms)", startupStopwatch.ElapsedMilliseconds);
            _logger.LogInformation("Starting background session monitor");
            startupStopwatch.Restart();
            await _host.StartAsync();
            _logger.LogInformation("Background session monitor started ({ElapsedMs} ms)", startupStopwatch.ElapsedMilliseconds);

            _sessionDefinitions = sessionResult.Sessions;
            _sessionNames = sessionResult.Sessions.Select(s => s.Name).ToArray();
            _monitorService = _host.Services.GetRequiredService<SessionMonitorService>();
            var stateStore = _host.Services.GetRequiredService<SessionStateStore>();
            _stateStore = stateStore;
            _conflictResolutionService = _host.Services.GetRequiredService<ConflictResolutionService>();
            _sessionEditingService = _host.Services.GetRequiredService<SessionEditingService>();
            _mutagenCliClient = _host.Services.GetRequiredService<MutagenCliClient>();
            _haltedRecoveryService = _host.Services.GetRequiredService<HaltedRecoveryService>();
            _conflictResolutionControllerLogger = _host.Services.GetRequiredService<ILogger<ConflictResolutionController>>();
            _notificationQueue = _host.Services.GetRequiredService<NotificationQueue>();

            startupStopwatch.Restart();
            _trayIconController = BuildAndStartTrayIconController(options, _sessionNames);
            _logger.LogInformation("MutagenMon startup complete — tray icon is live (controller started in {ElapsedMs} ms)", startupStopwatch.ElapsedMilliseconds);

            StartUpdateDetection();

            // FR-15.3: off-by-default opt-out of "no window shown at startup".
            if (options.ShowMainScreenAtStartup)
            {
                _logger.LogInformation("ShowMainScreenAtStartup is enabled; showing status window at startup");
                ShowStatusWindow();
            }
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
        _logger?.LogWarning("Self-restart requested (cause logged by the tray icon controller); restarting");
        // Hand the single-instance mutex over first: otherwise the new
        // process sees it held, signals us and exits, and this one then
        // shuts down too — leaving no process (and no tray icon) at all.
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
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
        if (_startupIconCreateFailed)
        {
            _startupIconCreateFailed = false;
            controller.RequestRecreate();
        }

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
        // TrayIconController.IsReloadInProgress is already false here, while the monitor stack is still rebuilt below.
        _reloadRebuildRunning = true;
        try
        {
            await RebuildAfterReloadAsync();
        }
        finally
        {
            _reloadRebuildRunning = false;
        }
    }

    private async Task RebuildAfterReloadAsync()
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

            // Dispose cancels the automatic fixes still running: wait until they have really stopped, so a recovery of
            // the old monitor cannot overlap one of the new services (FR-59.2: one recovery per session at a time).
            await _monitorService.WhenHaltedRecoveryIdleAsync();
        }

        // A manual recovery may be running too (it is not tied to the monitor): stop it and wait for it as well.
        if (_haltedRecoveryService is not null)
            await _haltedRecoveryService.StopAsync();

        var newSessionNames = newSessionResult.Sessions.Select(s => s.Name).ToArray();
        var optionsWrapper = Options.Create(newOptions);
        MutagenCliClient newCliClient = new MutagenCliClient(optionsWrapper, _host!.Services.GetRequiredService<ILogger<MutagenCliClient>>());
        ConflictFileClient newConflictFileClient = new ConflictFileClient(optionsWrapper, _host.Services.GetRequiredService<ILogger<ConflictFileClient>>());
        var newConflictResolutionService = new ConflictResolutionService(newConflictFileClient, _host.Services.GetRequiredService<ILogger<ConflictResolutionService>>());
        var newPurger = new EndpointPurger(optionsWrapper, _host.Services.GetRequiredService<ILogger<EndpointPurger>>());
        var newHaltedRecovery = new HaltedRecoveryService(
            newCliClient, newPurger, optionsWrapper, _host.Services.GetRequiredService<ILogger<HaltedRecoveryService>>());
        var newMonitorService = new SessionMonitorService(
            newCliClient, _stateStore!, optionsWrapper, newSessionResult.Sessions,
            _host.Services.GetRequiredService<FileTimestampProvider>(),
            newConflictResolutionService, _notificationQueue!,
            _host.Services.GetRequiredService<ILogger<SessionMonitorService>>(),
            newHaltedRecovery);
        await newMonitorService.StartAsync(CancellationToken.None);

        _options = newOptions;
        _sessionDefinitions = newSessionResult.Sessions;
        ApplyHaltedAutoFixDirectives(newSessionResult);
        _sessionNames = newSessionNames;
        _monitorService = newMonitorService;
        _conflictResolutionService = newConflictResolutionService;
        _sessionEditingService = new SessionEditingService(
            newCliClient, sessionsPath, _host.Services.GetRequiredService<ILogger<SessionEditingService>>());
        _mutagenCliClient = newCliClient;
        _haltedRecoveryService = newHaltedRecovery;

        // An Edit that only changed a directive may have finished while this reload was awaiting: it updated the old
        // monitor, so re-read the directives and give them to the new one.
        RefreshHaltedAutoFixDirectives();
        _trayIconController = BuildAndStartTrayIconController(newOptions, newSessionNames);
        // FR-56.2: the scripts file path may have changed with the new options.
        _statusWindow?.RefreshSessionScripts();
        _statusWindow?.RefreshSessionsFileLink();
        // FR-66.2: the deploy path may have changed with the new options.
        _ = CheckForUpdateAsync();
        _statusWindow?.SetHaltedRootEmptyingText(newOptions.HaltedOneSidedRootEmptying);

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

    /// <summary>Sessions file path (<c>MutagenSessionsBatFile</c>) from the
    /// current options, relative to the application directory.</summary>
    private string ResolveSessionsPath() =>
        Path.Combine(AppContext.BaseDirectory, _options!.MutagenSessionsBatFile.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Scripts file path from the current options, relative to the
    /// application directory like <c>MutagenSessionsBatFile</c> (FR-49).</summary>
    private string ResolveScriptsPath() =>
        Path.Combine(AppContext.BaseDirectory, _options!.ScriptsFile.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>First configured SSH server host, or null when none is set
    /// (prefills the host of a new script).</summary>
    private string? FirstSshServerHost() =>
        _options?.SshServers.Select(server => server.Host.Trim()).FirstOrDefault(host => host.Length > 0);

    /// <summary>Built per run: <see cref="ScriptRunner"/> reads the ssh path
    /// and timeout once, so a fresh one picks up a reloaded config.</summary>
    private ScriptRunner CreateScriptRunner() =>
        new(Options.Create(_options!), _host!.Services.GetRequiredService<ILogger<ScriptRunner>>());

    /// <summary>Saves the status window's new size into the configuration
    /// file, rewriting only those two keys. Skipped when it equals the size
    /// already in the options, which is what a restore at startup or a
    /// maximize/restore produces.</summary>
    private void OnStatusWindowSizeSettled(object? sender, Size size)
    {
        var width = (int)Math.Round(size.Width);
        var height = (int)Math.Round(size.Height);
        if (_options is null || (_options.StatusWindowWidth == width && _options.StatusWindowHeight == height))
            return;

        var configPath = Path.Combine(AppContext.BaseDirectory, "config", "config_mutagenmon.json");
        try
        {
            ConfigLoader.SaveWindowSize(configPath, width, height);
            _options.StatusWindowWidth = width;
            _options.StatusWindowHeight = height;
            _logger?.LogDebug("Status window size saved: {Width}x{Height}", width, height);
        }
        catch (Exception ex)
        {
            // Not worth a dialog: the window size is a convenience, and the next resize retries.
            _logger?.LogWarning(ex, "Failed to save the status window size to '{Path}'", configPath);
        }
    }

    private void ShowStatusWindow()
    {
        if (_statusWindow is null)
        {
            _statusWindow = new StatusWindow(_logger!, _iconCache!, _loggerProvider!, _options?.ShowGenerateException ?? false,
                ResolveScriptsPath, ResolveSessionsPath, CreateScriptRunner, FirstSshServerHost);
            _statusWindow.ResolveConflictsRequested += OnResolveConflictsRequested;
            _statusWindow.ReloadConfigRequested += OnStatusWindowReloadConfigRequested;
            _statusWindow.ToggleMonitoringRequested += OnStatusWindowToggleMonitoringRequested;
            _statusWindow.ExitRequested += OnStatusWindowExitRequested;
            _statusWindow.UpdateRequested += OnStatusWindowUpdateRequested;
            _statusWindow.CheckUpdateRequested += OnStatusWindowCheckUpdateRequested;
            _statusWindow.AddSessionRequested += OnAddSessionRequested;
            _statusWindow.DuplicateSessionRequested += OnDuplicateSessionRequested;
            _statusWindow.ViewSyncStatusRequested += OnViewSyncStatusRequested;
            _statusWindow.EditSessionRequested += OnEditSessionRequested;
            _statusWindow.DeleteSessionRequested += OnDeleteSessionRequested;
            _statusWindow.AcceptDeletionRequested += OnAcceptDeletionRequested;
            _statusWindow.SizeSettled += OnStatusWindowSizeSettled;
            if (_options is not null)
            {
                _statusWindow.RestoreSize(_options.StatusWindowWidth, _options.StatusWindowHeight);
                _statusWindow.SetHaltedRootEmptyingText(_options.HaltedOneSidedRootEmptying);
            }
        }
        if (_stateStore is not null)
            _statusWindow.UpdateContent(_stateStore.Get(), _sessionNames, _trayIconController?.IsReloadInProgress ?? false);
        _statusWindow.SetUpdateFeatureEnabled(!string.IsNullOrWhiteSpace(_options?.DeployPath));
        _statusWindow.SetUpdateAvailable(_updateResult.IsAvailable);
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

        var window = new SessionEditWindow(null, _sessionNames, _options?.SshServers ?? new List<SshServerEntry>(), _logger) { Owner = _statusWindow };
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

    /// <summary>Handles the toolbar's "Duplicate" action: same as
    /// <see cref="OnAddSessionRequested"/> (a new session, created via
    /// <c>SessionEditingService.AddAsync</c>, not an edit of the source
    /// session), but the form starts pre-populated from the selected
    /// session's parsed definition instead of blank. The name is cleared so
    /// the FR-18.2 uniqueness check forces the user to pick a new one before
    /// Save enables — saving with the source's own name would collide with
    /// it.</summary>
    private void OnDuplicateSessionRequested(object? sender, string name)
    {
        if (_sessionEditingService is null || _statusWindow is null || _logger is null)
            return;

        var definition = _sessionDefinitions.FirstOrDefault(d => d.Name == name);
        if (definition is null)
        {
            _logger.LogWarning("Duplicate requested for unknown session '{Name}' (already removed?)", name);
            return;
        }

        var model = SessionCommandLineParser.Parse(definition.RawCreateCommand);
        model.HaltedAutoFix = _haltedAutoFixDirectives.TryGetValue(name, out var sourceFlag) ? sourceFlag : null;
        model.Name = "";
        var window = new SessionEditWindow(model, _sessionNames, _options?.SshServers ?? new List<SshServerEntry>(), _logger) { Owner = _statusWindow, Title = $"MutagenMon: Duplicate session {name}" };
        window.SaveRequested += async (_, _) =>
        {
            window.SetBusy(true);
            try
            {
                await _sessionEditingService.AddAsync(window.Result!, CancellationToken.None);
                _logger.LogInformation("Session '{Source}' duplicated as: {Name}", name, window.Result!.Name);
                window.CompleteSave();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to duplicate session '{Source}' as '{Name}'", name, window.Result!.Name);
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
        model.HaltedAutoFix = _haltedAutoFixDirectives.TryGetValue(name, out var currentFlag) ? currentFlag : null;
        var window = new SessionEditWindow(model, _sessionNames, _options?.SshServers ?? new List<SshServerEntry>(), _logger) { Owner = _statusWindow };
        var outcome = SessionEditOutcome.Recreated;
        window.SaveRequested += async (_, _) =>
        {
            window.SetBusy(true);
            try
            {
                outcome = await _sessionEditingService.EditAsync(window.OriginalName!, window.Result!, CancellationToken.None);
                _logger.LogInformation("Session edited: {OldName} -> {NewName} ({Outcome})", window.OriginalName, window.Result!.Name, outcome);
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

        if (window.ShowDialog() != true)
            return;

        // FR-65.3: an Edit that only changed the `@halted-auto-fix` directive left the session running, so
        // nothing is restarted: only the cached directives are re-read.
        if (outcome == SessionEditOutcome.DirectiveOnly)
            RefreshHaltedAutoFixDirectives();
        else
            ReloadConfig();
    }

    /// <summary>FR-66.1: adds the missing update-detection keys to the config
    /// file; the in-memory options already hold the same defaults. A file that
    /// cannot be written only costs a warning.</summary>
    private void EnsureUpdateConfigKeys(string configPath)
    {
        try
        {
            var added = ConfigLoader.EnsureUpdateKeys(configPath);
            if (added.Count > 0)
                _logger?.LogInformation("Added the missing update-detection key(s) to {ConfigPath}: {Keys}", configPath, string.Join(", ", added));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger?.LogWarning(ex, "Could not add the missing update-detection key(s) to {ConfigPath}; the built-in defaults apply", configPath);
        }
    }

    /// <summary>FR-66.2: checks once now, then every <see cref="UpdateCheckInterval"/>.</summary>
    private void StartUpdateDetection()
    {
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += async (_, _) =>
        {
            _nextScheduledUpdateCheck = (DateTime.UtcNow + UpdateCheckInterval).ToLocalTime();
            await CheckForUpdateAsync();
        };
        _nextScheduledUpdateCheck = (DateTime.UtcNow + UpdateCheckInterval).ToLocalTime();
        _updateTimer.Start();
        _ = CheckForUpdateAsync();
    }

    /// <summary>FR-66.2/FR-66.3: compares the deployed executable with the running one off the UI thread (the
    /// deploy folder may be a slow network share), then switches the toolbar button between "Check for new version"
    /// and "Update available". Never raises: an
    /// unreachable folder only means "no update known".</summary>
    private async Task CheckForUpdateAsync()
    {
        var options = _options;
        if (options is null)
            return;

        // A check already running (slow share) may be for an older DeployPath: run another one right after it.
        if (_updateCheckRunning)
        {
            _updateCheckPending = true;
            return;
        }

        _updateCheckRunning = true;
        if (_updateCheckIdle.Task.IsCompleted)
            _updateCheckIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var deployPath = options.DeployPath;
            var runningExe = Environment.ProcessPath;
            var result = await Task.Run(() => UpdateChecker.Check(deployPath, runningExe, AppContext.BaseDirectory));
            // A reload that changed the options while this check ran makes the result obsolete: the re-run replaces it.
            if (!ReferenceEquals(options, _options))
                return;

            _updateResult = result;
            _statusWindow?.SetUpdateFeatureEnabled(!string.IsNullOrWhiteSpace(deployPath));
            _statusWindow?.SetUpdateAvailable(result.IsAvailable);
            LogUpdateCheckFailure(result.FailureReason);
            if (!string.IsNullOrWhiteSpace(deployPath))
                _logger?.LogInformation("Next scheduled update check at {NextCheck:yyyy-MM-dd HH:mm:ss}", _nextScheduledUpdateCheck);
            if (!result.IsAvailable)
                return;

            _logger?.LogInformation(
                "Update available: {DeployedExe} ({DeployedUtc:u}) is newer than the running executable ({CurrentUtc:u})",
                result.DeployedExePath, result.DeployedWriteTimeUtc, result.CurrentWriteTimeUtc);

            // FR-66.5: one notification per deployed build, not one per check.
            if (options.NotifyUpdateAvailable && _lastNotifiedUpdateUtc != result.DeployedWriteTimeUtc)
            {
                _lastNotifiedUpdateUtc = result.DeployedWriteTimeUtc;
                _notificationQueue?.Enqueue(new NotificationMessage("Update available", "A newer MutagenMon is available. Open the status window to update."));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The update check failed");
            if (ReferenceEquals(options, _options))
            {
                _updateResult = UpdateCheckResult.None with { FailureReason = ex.Message };
                _statusWindow?.SetUpdateAvailable(false);
            }
        }
        finally
        {
            _updateCheckRunning = false;
            if (_updateCheckPending)
            {
                _updateCheckPending = false;
                _ = CheckForUpdateAsync();
            }
            else
                _updateCheckIdle.TrySetResult();
        }
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    /// <summary>FR-66.3: logs why a check could not compare the builds, once per distinct reason so that the 2-hour
    /// checks do not repeat the same line.</summary>
    private void LogUpdateCheckFailure(string? reason)
    {
        if (reason == _lastUpdateFailureReason)
            return;

        _lastUpdateFailureReason = reason;
        if (reason is not null)
            _logger?.LogWarning("Update check: no comparison possible, {Reason}", reason);
    }

    private async void OnStatusWindowUpdateRequested(object? sender, EventArgs e) => await UpdateAsync();

    /// <summary>FR-66.6: the user asked for a check now. When a newer build is found the confirmation of the update
    /// opens directly (the button turns into "Update available" in case it is cancelled); otherwise a message says
    /// that MutagenMon is up to date, or why no comparison was possible.</summary>
    private async void OnStatusWindowCheckUpdateRequested(object? sender, EventArgs e)
    {
        var owner = _statusWindow;
        if (owner is null)
            return;

        owner.SetUpdateCheckInProgress(true);
        try
        {
            await CheckForUpdateAsync();
            // A check already running (or a re-run queued by a reload) answers for this click: wait for the last one.
            await _updateCheckIdle.Task;
        }
        finally
        {
            owner.SetUpdateCheckInProgress(false);
        }

        var result = _updateResult;
        if (result.IsAvailable)
        {
            // Straight to the confirmation of FR-66.7: the user asked for the new version.
            await UpdateAsync();
            return;
        }

        // A reload emptied DeployPath while the check ran: the feature is off and the button is hidden.
        if (string.IsNullOrWhiteSpace(_options?.DeployPath))
            return;

        var message = result.FailureReason is null
            ? "MutagenMon is up to date."
            : $"No comparison was possible: {result.FailureReason}";
        GenericMessageDialog.ShowInfo(owner, _logger!, "MutagenMon — check for new version", message);
    }

    /// <summary>FR-66.6/FR-66.7: confirms, writes the helper script that waits for this process to exit, copies the
    /// deployed files over the installation and starts MutagenMon again, launches it, then shuts down like Exit.</summary>
    private async Task UpdateAsync()
    {
        var update = _updateResult;
        var owner = _statusWindow;
        if (!update.IsAvailable || update.DeployedExePath is null || owner is null || _options is null)
            return;

        if (IsUpdateBlocked())
        {
            _logger?.LogInformation("Update postponed: a configuration reload or an update check is in progress");
            return;
        }

        var confirmed = GenericMessageDialog.ShowConfirm(
            owner,
            _logger!,
            "MutagenMon — confirm update",
            $"A newer version of MutagenMon is available:\n\nDeployed: {update.DeployedWriteTimeUtc?.ToLocalTime():g}\nRunning:  {update.CurrentWriteTimeUtc?.ToLocalTime():g}\n\n" +
            "MutagenMon will close, update its files and restart.\nBackground synchronization will continue.",
            okLabel: "Update",
            cancelLabel: "Cancel");
        if (!confirmed)
        {
            _logger?.LogInformation("User action: update cancelled at confirmation");
            return;
        }

        // A reload may have started while the modal dialog was open.
        if (IsUpdateBlocked())
        {
            _logger?.LogInformation("Update postponed: a configuration reload or an update check is in progress");
            return;
        }

        // Same rule as the exit (FR-47.5): the edits of the scripts are kept when they cannot be saved.
        if (!owner.TrySaveScripts())
        {
            _logger?.LogWarning("Update cancelled: the scripts with unsaved changes could not be saved");
            return;
        }

        try
        {
            StartUpdateScript(Path.GetDirectoryName(update.DeployedExePath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _logger?.LogError(ex, "The update script could not be started");
            DeleteUpdateScript();
            GenericMessageDialog.ShowInfo(owner, _logger!, "MutagenMon — update failed", $"The update could not be started:\n{ex.Message}");
            return;
        }

        _logger?.LogInformation("Update script started; shutting down so that it can replace the files");
        await ShutdownAsync();
    }

    /// <summary>True from the reload request until the new monitor stack runs: shutting down meanwhile would race it.</summary>
    private bool IsReloadRunning() => _reloadRebuildRunning || _trayIconController?.IsReloadInProgress == true;

    /// <summary>A reload, or a check that may still replace the result (e.g. a new DeployPath), makes the
    /// current update result unreliable: the update must wait.</summary>
    private bool IsUpdateBlocked() => IsReloadRunning() || _updateCheckRunning || _updateCheckPending;

    private static string UpdateScriptPath => Path.Combine(Path.GetTempPath(), $"MutagenMon-update-{Environment.ProcessId}.cmd");

    private static void DeleteUpdateScript()
    {
        try
        {
            File.Delete(UpdateScriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover script in the temp folder is harmless.
        }
    }

    private void StartUpdateScript(string deployDir)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running executable is unknown.");
        var installDir = AppContext.BaseDirectory;
        var tempDir = Path.GetTempPath();
        var scriptPath = UpdateScriptPath;
        var logPath = Path.Combine(tempDir, "MutagenMon-update.log");

        File.WriteAllText(scriptPath, UpdateScriptBuilder.Build(Environment.ProcessId, deployDir, installDir, exePath, logPath));
        using var scriptProcess = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{scriptPath}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>FR-63.1: adds the missing halted-session keys to the config
    /// file. The in-memory options already hold the same defaults, so no
    /// reload is needed; a file that cannot be written only costs a warning.</summary>
    private void EnsureHaltedConfigKeys(string configPath)
    {
        try
        {
            var added = ConfigLoader.EnsureHaltedKeys(configPath);
            if (added.Count > 0)
                _logger?.LogInformation("Added the missing halted-session key(s) to {ConfigPath}: {Keys}", configPath, string.Join(", ", added));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger?.LogWarning(ex, "Could not add the missing halted-session key(s) to {ConfigPath}; the built-in defaults apply", configPath);
        }
    }

    /// <summary>Keeps the directives of a freshly parsed sessions file and
    /// logs the ones that were ignored (FR-64.4).</summary>
    private void ApplyHaltedAutoFixDirectives(SessionDefinitionLoadResult result)
    {
        _haltedAutoFixDirectives = result.HaltedAutoFixDirectives;
        foreach (var warning in result.DirectiveWarnings)
            _logger?.LogWarning("{Warning}", warning);
    }

    private void RefreshHaltedAutoFixDirectives()
    {
        if (_options is null)
            return;

        try
        {
            var sessionsPath = Path.Combine(AppContext.BaseDirectory, _options.MutagenSessionsBatFile.Replace('/', Path.DirectorySeparatorChar));
            var result = SessionDefinitionLoader.ParseFile(sessionsPath);
            ApplyHaltedAutoFixDirectives(result);

            // FR-65.3: the running monitor must see the new flag too, without any session being restarted.
            _monitorService?.UpdateHaltedAutoFixOverrides(result.HaltedAutoFixDirectives);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not re-read the @halted-auto-fix directives after an Edit");
        }
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

    /// <summary>Handles a row's "Accept deletion" icon (FR-60): after a
    /// confirmation that names both sides and the number of files to be
    /// deleted, runs the recovery of FR-59 — purge the side that was not
    /// emptied, then reset the session. It does not depend on the auto-fix
    /// flags or the cooldown: it is an explicit user decision. A success shows
    /// nothing (the row returns to normal on a later poll); a refusal or a
    /// failure shows an error dialog (FR-60.4).</summary>
    private async void OnAcceptDeletionRequested(object? sender, string name)
    {
        try
        {
            await AcceptDeletionAsync(name);
        }
        catch (OperationCanceledException)
        {
            // The configuration is being reloaded: the recovery was stopped on purpose, but the user clicked Yes and the
            // purge may be partial, so say so (FR-60.4).
            _logger?.LogWarning("Accept deletion of session '{Name}' was cancelled by a reload of the configuration", name);
            if (_statusWindow is not null)
                MessageBox.Show(
                    _statusWindow,
                    $"The recovery of session '{name}' was interrupted by the reload of the configuration.\n\n"
                    + "The side to purge may be partly emptied. Check the session and accept the deletion again if it is still halted.",
                    "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Accept deletion failed unexpectedly for session '{Name}'", name);
            if (_statusWindow is not null)
                MessageBox.Show(
                    _statusWindow, $"MutagenMon could not recover session '{name}':\n\n{ex.Message}",
                    "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task AcceptDeletionAsync(string name)
    {
        if (_haltedRecoveryService is null || _stateStore is null || _statusWindow is null || _logger is null)
            return;

        // The snapshot the poll took when the halt was reported (FR-58.3).
        _stateStore.Get().SessionStatuses.TryGetValue(name, out var status);
        var problem = _haltedRecoveryService.CheckRecoverable(status, out var analysis);
        if (problem is not null)
        {
            _logger.LogWarning("Accept deletion refused for '{Name}': {Problem}", name, problem);
            MessageBox.Show(
                _statusWindow, $"MutagenMon cannot accept the deletion for session '{name}': {problem}.",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var body = $"Session {name}: {analysis.Emptied!.Url} was emptied.\n\n"
            + $"The content of {analysis.Purged!.Url} ({analysis.PurgedFileCount} file(s)) will be deleted and the session reset.\n\nContinue ?";
        if (!GenericMessageDialog.ShowConfirm(_statusWindow, _logger, "Mutagen accept deletion", body, okLabel: "Yes", cancelLabel: "No"))
            return;

        // The dialog above can stay open for a long time: only purge from what the latest poll shows now, and only if
        // it is still the same halt between the same two sides. Otherwise the session may be synchronising again, and
        // emptying one of its sides would be propagated as ordinary deletions (FR-60.3).
        var snapshot = _stateStore.Get();
        snapshot.SessionStatuses.TryGetValue(name, out var freshStatus);
        var changed = DateTimeOffset.UtcNow - snapshot.LastSuccessfulPollUtc > TimeSpan.FromSeconds(30)
            ? "the status of the sessions is not up to date"
            : _haltedRecoveryService.CheckUnchanged(name, analysis, freshStatus, snapshot.PollStartedUtc);
        if (changed is not null)
        {
            _logger.LogWarning("Accept deletion cancelled for '{Name}': {Reason}", name, changed);
            MessageBox.Show(
                _statusWindow, $"Nothing was deleted: {changed}.\n\nCheck the session and try again if it is still halted.",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = await _haltedRecoveryService.RecoverAsync(name, freshStatus, CancellationToken.None);
        if (result.Success)
        {
            _logger.LogInformation("Accept deletion done for session '{Name}'", name);
            return;
        }

        _logger.LogError("Accept deletion failed for session '{Name}' at step '{Step}': {Message}", name, result.Step, result.Message);
        MessageBox.Show(
            _statusWindow, $"MutagenMon could not recover session '{name}' (step: {result.Step}):\n\n{result.Message}",
            "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // FR-47.5: the exit saves the scripts with unsaved changes. When the save fails the application keeps
        // running (a message says why), so the edits are not lost.
        if (_statusWindow is not null && !_statusWindow.TrySaveScripts())
        {
            _logger?.LogWarning("Exit cancelled: the scripts with unsaved changes could not be saved");
            return;
        }

        _logger?.LogInformation("User action: exit requested; shutting down");
        await ShutdownAsync();
    }

    /// <summary>Hides the UI, stops the monitor host and ends the process — the part of the exit that follows
    /// the confirmation, shared with the update (FR-66.7).</summary>
    private async Task ShutdownAsync()
    {
        _updateTimer?.Stop();
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

    /// <summary>Loads the app's configuration — thin wrapper kept so every
    /// call site in this file reads the same as before;
    /// <see cref="ConfigLoader"/> (in Core) is shared with the "Mutagen
    /// monitor Configuration" tab's Check/Save (<see cref="MutagenMonitorConfigEditorView"/>).</summary>
    private static MutagenMonOptions LoadConfig(string path) => ConfigLoader.Load(path);
}

using System.IO;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.ProfileWatch;
using MutagenMon.Core.Sessions;

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
/// capturing every level (Debug and above) in one primary file, configured
/// in two stages: constructed with a default path so failures are visible
/// even before config is read, then re-pointed via
/// <see cref="FileLoggerProvider.SetPrimaryLogPath"/> once
/// <see cref="MutagenMonOptions"/> is loaded to honor the configured
/// LOG_PATH. Every startup step logs at Information so a failed launch
/// (e.g. missing config/session file, tray icon creation failure) is always
/// traceable in that file instead of failing silently — see global
/// exception handlers below.
/// </summary>
public partial class App : Application
{
    private FileLoggerProvider? _loggerProvider;
    private ILogger<App>? _logger;
    private IHost? _host;
    private TrayIconController? _trayIconController;
    private StatusWindow? _statusWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var baseDir = AppContext.BaseDirectory;
        var fatalLogPath = Path.Combine(baseDir, "mutagenMon.fatal.log");
        _loggerProvider = new FileLoggerProvider(ResolveLogFilePath(baseDir, "log"), fatalLogPath);
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
            var configPath = Path.Combine(baseDir, "config", "config_mutagenmon.json");
            _logger.LogInformation("Loading configuration from {ConfigPath}", configPath);
            var options = ConfigLoader.Load(configPath);
            _loggerProvider.SetPrimaryLogPath(ResolveLogFilePath(baseDir, options.LogPath));
            _logger.LogInformation(
                "Configuration loaded: PollPeriod={PollPeriodMs}ms, StartEnabled={StartEnabled}, LogPath={LogPath}",
                options.MutagenPollPeriodMs, options.StartEnabled, options.LogPath);

            var sessionsPath = Path.Combine(baseDir, options.MutagenSessionsBatFile.Replace('/', Path.DirectorySeparatorChar));
            _logger.LogInformation("Loading session definitions from {SessionsPath}", sessionsPath);
            var sessionResult = SessionDefinitionLoader.ParseFile(sessionsPath);
            _logger.LogInformation("Loaded {SessionCount} session definition(s): {SessionNames}",
                sessionResult.Sessions.Count, string.Join(", ", sessionResult.Sessions.Select(s => s.Name)));
            foreach (var duplicate in sessionResult.DuplicateNames)
            {
                _logger.LogWarning("Duplicate session name in {File}: {Name}", sessionsPath, duplicate);
            }

            _logger.LogInformation("Building application host");
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(_loggerProvider);
            builder.Services.AddSingleton(Options.Create(options));
            builder.Services.AddSingleton<IReadOnlyList<SessionDefinition>>(sessionResult.Sessions);
            builder.Services.AddSingleton<IMutagenCliClient, MutagenCliClient>();
            builder.Services.AddSingleton<ISessionStateStore, SessionStateStore>();
            builder.Services.AddSingleton<IFileTimestampProvider, FileTimestampProvider>();
            builder.Services.AddHostedService<SessionMonitorService>();

            _host = builder.Build();
            _logger.LogInformation("Starting background session monitor");
            await _host.StartAsync();
            _logger.LogInformation("Background session monitor started");

            var stateStore = _host.Services.GetRequiredService<ISessionStateStore>();
            var iconCache = new IconImageCache(Path.Combine(baseDir, "Assets", "Icons"));
            _logger.LogInformation("Acquiring tray icon resource");
            var trayIcon = (TaskbarIcon)Resources["TrayIcon"];
            // With no main window, TaskbarIcon's native icon is never created
            // implicitly (it normally happens on Loaded, when a control enters
            // a live visual tree — which never happens for a resource that is
            // only ever referenced from code). ForceCreate() is the pattern
            // H.NotifyIcon's own "windowless" sample app uses for exactly this
            // case; without it, the app runs with no visible tray icon at all.
            trayIcon.ForceCreate();
            _logger.LogInformation("Tray icon resource created (ForceCreate)");
            var trayIconLogger = _host.Services.GetRequiredService<ILogger<TrayIconController>>();

            _trayIconController = new TrayIconController(
                trayIcon, stateStore, iconCache, options.TrayTooltip, options.StatusMaxLag.ToLagThresholds(),
                OnSelfRestartNeeded, trayIconLogger);
            _trayIconController.Start();
            _logger.LogInformation("MutagenMon startup complete — tray icon is live");
        }
        catch (Exception ex)
        {
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
        _statusWindow ??= new StatusWindow();
        _statusWindow.Show();
        _statusWindow.Activate();
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
            $"MutagenMon hit an unexpected error and will close:\n\n{e.Exception}",
            "MutagenMon — error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.LogCritical(ex, "Unhandled exception on a background thread");
        }
        else
        {
            _logger?.LogCritical("Unhandled exception on a background thread: {ExceptionObject}", e.ExceptionObject);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    /// <summary>Resolves logPath to a log directory (relative to baseDir
    /// unless logPath is itself rooted/absolute), creates it if needed, and
    /// returns the full path to mutagenMon.log inside it.</summary>
    private static string ResolveLogFilePath(string baseDir, string logPath)
    {
        var logDir = Path.IsPathRooted(logPath) ? logPath : Path.Combine(baseDir, logPath);
        Directory.CreateDirectory(logDir);
        return Path.Combine(logDir, "mutagenMon.log");
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Status;

namespace MutagenMon.App;

/// <summary>
/// Implements the full session status view (FR-8), with a conflicts section and "Resolve
/// conflicts" action when at least one conflict is not auto-resolved
/// (FR-8.1/FR-8.2), or a plain informational OK dialog otherwise (FR-8.3).
/// Closing hides rather than closes the window, so re-opening it doesn't
/// reconstruct it from scratch — matches NFR-7's "no main window, dialogs
/// on demand" model.
/// </summary>
public partial class StatusWindow : Window
{
    /// <summary>Raised when the user clicks "Resolve conflicts" (FR-8.2) —
    /// the actual FR-9 workflow is composed and run by the caller
    /// (App.xaml.cs), which owns the DI-provided services it needs.</summary>
    public event EventHandler? ResolveConflictsRequested;

    /// <summary>Raised when the user clicks "Reload config" — handled by
    /// App.xaml.cs, which owns the monitor service and tray icon controller
    /// (FR-7.1).</summary>
    public event EventHandler? ReloadConfigRequested;

    /// <summary>Raised when the user clicks "Stop/Start Mutagen sessions" —
    /// handled by App.xaml.cs, which owns the monitor service (FR-7.2).</summary>
    public event EventHandler? ToggleMonitoringRequested;

    /// <summary>Raised when the user clicks "Exit" — handled by App.xaml.cs,
    /// which owns the host lifetime.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Bound once to <c>SessionsGrid.ItemsSource</c> and updated
    /// in place on every refresh (<see cref="SyncRows"/>) rather than
    /// replaced — while the window stays open it's refreshed roughly once a
    /// second (FR-8.4), and rebinding a fresh list every tick would rebuild
    /// every row (losing selection/scroll position, and flickering) even
    /// when nothing actually changed.</summary>
    private readonly ObservableCollection<SessionSummaryRow> _sessionRows = new();
    private readonly ILogger _logger;

    public StatusWindow(ILogger logger, IconImageCache iconCache)
    {
        InitializeComponent();
        SessionsGrid.ItemsSource = _sessionRows;
        ((IconKeyToImageSourceConverter)Resources["IconKeyToImageSourceConverter"]).Cache = iconCache;
        _logger = logger;
    }

    public void UpdateContent(MonitorSnapshot snapshot, IReadOnlyList<string> sessionNames, bool reloadInProgress)
    {
        var rows = StatusReportFormatter.BuildSessionRows(
            sessionNames, snapshot.SessionStatuses, snapshot.LastChangedUtc, snapshot.SessionCodes, snapshot.Enabled);
        SyncRows(_sessionRows, rows);

        ConflictsText.Text = StatusReportFormatter.BuildConflictsSection(sessionNames, snapshot.Conflicts);

        var hasUnresolvedConflicts = StatusReportFormatter.HasUnresolvedConflicts(snapshot.Conflicts);
        CloseButton.Visibility = hasUnresolvedConflicts ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;
        ResolveConflictsButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;

        ToggleMonitoringButton.Content = snapshot.Enabled ? "Stop Mutagen sessions" : "Start Mutagen sessions";

        // Mirrors FR-7.5's tray-menu collapse while a reload drains and
        // rebuilds the monitor stack — prevents firing a second reload or
        // toggling monitoring against a service that's about to be replaced.
        ReloadConfigButton.IsEnabled = !reloadInProgress;
        ToggleMonitoringButton.IsEnabled = !reloadInProgress;
        ExitButton.IsEnabled = !reloadInProgress;
    }

    /// <summary>Updates <paramref name="target"/> to match
    /// <paramref name="source"/> element-by-element (index-based, since row
    /// order is always <c>sessionNames</c> order) instead of clearing and
    /// re-adding everything — <see cref="SessionSummaryRow"/> is a record,
    /// so the equality check skips any row whose data is unchanged since the
    /// last refresh, avoiding needless DataGrid re-rendering.</summary>
    private static void SyncRows(ObservableCollection<SessionSummaryRow> target, IReadOnlyList<SessionSummaryRow> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i >= target.Count)
                target.Add(source[i]);
            else if (!target[i].Equals(source[i]))
                target[i] = source[i];
        }

        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Close clicked");
        Hide();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Cancel clicked");
        Hide();
    }

    private void OnResolveConflictsClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: resolve conflicts clicked");
        ResolveConflictsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReloadConfigClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Reload config clicked");
        ReloadConfigRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleMonitoringClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Stop/Start sessions clicked");
        ToggleMonitoringRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Exit clicked");
        // Not hidden here: the caller (App.ExitAsync) asks for confirmation
        // first and only hides/closes everything once the user confirms.
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

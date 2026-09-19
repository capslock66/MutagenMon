using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
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

    /// <summary>Raised when the user clicks the toolbar's "Add" action
    /// (FR-16.1) — handled by App.xaml.cs, which owns
    /// <c>SessionEditingService</c> and the session definitions.</summary>
    public event EventHandler? AddSessionRequested;

    /// <summary>Raised when the user clicks a row's View sync status icon
    /// (FR-28), with that session's name.</summary>
    public event EventHandler<string>? ViewSyncStatusRequested;

    /// <summary>Raised when the user clicks a row's Edit icon (FR-17.2),
    /// with that session's name.</summary>
    public event EventHandler<string>? EditSessionRequested;

    /// <summary>Raised when the user clicks a row's Delete icon (FR-17.3),
    /// with that session's name.</summary>
    public event EventHandler<string>? DeleteSessionRequested;

    /// <summary>Bound once to <c>SessionsGrid.ItemsSource</c> and updated
    /// in place on every refresh (<see cref="SyncRows"/>) rather than
    /// replaced — while the window stays open it's refreshed roughly once a
    /// second (FR-8.4), and rebinding a fresh list every tick would rebuild
    /// every row (losing selection/scroll position, and flickering) even
    /// when nothing actually changed.</summary>
    private readonly ObservableCollection<SessionSummaryRow> _sessionRows = new();
    private readonly ILogger _logger;

    public StatusWindow(ILogger logger, IconImageCache iconCache, FileLoggerProvider loggerProvider)
    {
        InitializeComponent();
        SessionsGrid.ItemsSource = _sessionRows;
        ((IconKeyToImageSourceConverter)Resources["IconKeyToImageSourceConverter"]).Cache = iconCache;
        _logger = logger;
        MutagenConfigEditorViewControl.Initialize(logger);
        MutagenMonitorConfigEditorViewControl.Initialize(logger);
        LogsViewControl.Initialize(logger, loggerProvider);
    }

    public void UpdateContent(MonitorSnapshot snapshot, IReadOnlyList<string> sessionNames, bool reloadInProgress)
    {
        var rows = BuildSessionRows(
            sessionNames, snapshot.SessionStatuses, snapshot.LastChangedUtc, snapshot.SessionCodes, snapshot.Enabled);
        SyncRows(_sessionRows, rows);

        ConflictsText.Text = BuildConflictsSection(sessionNames, snapshot.Conflicts);

        var hasUnresolvedConflicts = HasUnresolvedConflicts(snapshot.Conflicts);
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

    private void OnAddSessionClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Add session clicked");
        AddSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewSyncStatusClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SessionSummaryRow row)
            return;
        _logger.LogInformation("User action: status window View sync status clicked ({Name})", row.Name);
        ViewSyncStatusRequested?.Invoke(this, row.Name);
    }

    private void OnEditSessionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SessionSummaryRow row)
            return;
        _logger.LogInformation("User action: status window Edit session clicked ({Name})", row.Name);
        EditSessionRequested?.Invoke(this, row.Name);
    }

    private void OnDeleteSessionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SessionSummaryRow row)
            return;
        _logger.LogInformation("User action: status window Delete session clicked ({Name})", row.Name);
        DeleteSessionRequested?.Invoke(this, row.Name);
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

    /// <summary>One row per session (FR-8.1), in <paramref name="sessionNames"/>
    /// order, for the status view's grid.</summary>
    private static IReadOnlyList<SessionSummaryRow> BuildSessionRows(
        IReadOnlyCollection<string> sessionNames,
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses,
        IReadOnlyDictionary<string, DateTimeOffset?> lastChangedUtc,
        IReadOnlyDictionary<string, SessionStatusCode> sessionCodes,
        bool enabled)
    {
        var rows = new List<SessionSummaryRow>();
        foreach (var name in sessionNames)
        {
            statuses.TryGetValue(name, out var status);
            lastChangedUtc.TryGetValue(name, out var lastChanged);
            sessionCodes.TryGetValue(name, out var code);
            rows.Add(new SessionSummaryRow(
                name,
                TrayIconStateResolver.ResolveSessionIconKey(code, enabled),
                BuildStatusDisplay(status),
                status?.Alpha?.Url ?? "(unknown)",
                status?.Beta?.Url ?? "(unknown)",
                lastChanged));
        }

        return rows;
    }

    /// <summary>While a large file is actively transferring, the bare
    /// "Staging files on &lt;side&gt;" status text never changes and gives no
    /// indication of progress (FR-2.2). When <see cref="StagingProgress"/> is
    /// available, show which file is currently uploading and how much data
    /// has moved instead. <see cref="StagingProgress.FilesCompleted"/> counts
    /// fully-finished files, so it is bumped by one here to report the file
    /// *in progress* (e.g. "0/2" while the first of two files is uploading
    /// becomes "1/2", not "0/2").</summary>
    private static string BuildStatusDisplay(ParsedSessionStatus? status)
    {
        if (status is null || string.IsNullOrEmpty(status.Status))
            return "(not running)";

        if (status.Staging is { CurrentFileName: { } fileName } staging)
        {
            var filesInProgress = Math.Min(staging.FilesCompleted + 1, staging.FilesTotal);
            return $"Uploading {filesInProgress}/{staging.FilesTotal} , {fileName} , {staging.BytesTransferred}";
        }

        return status.Status;
    }

    /// <summary>The "==== CONFLICTS ====" section, listing every conflict
    /// (autoresolving ones annotated) — empty string if there are none at all.</summary>
    private static string BuildConflictsSection(
        IReadOnlyCollection<string> sessionNames, IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts)
    {
        var sb = new StringBuilder();
        foreach (var name in sessionNames)
        {
            if (!conflicts.TryGetValue(name, out var list))
                continue;
            foreach (var conflict in list)
            {
                sb.Append(name).Append(": ").Append(conflict.AlphaName);
                if (conflict.AutoResolved)
                    sb.Append(" [autoresolving]");
                sb.Append('\n');
            }
        }

        if (sb.Length == 0)
            return "";
        return "==================== CONFLICTS ====================\n" + sb;
    }

    /// <summary>True if at least one conflict is
    /// NOT auto-resolved — this, not the mere presence of a conflict, decides
    /// whether the status view offers "Resolve conflicts" (FR-8.2) or is
    /// purely informational (FR-8.3).</summary>
    private static bool HasUnresolvedConflicts(IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts) =>
        conflicts.Values.Any(list => list.Any(c => !c.AutoResolved));
}

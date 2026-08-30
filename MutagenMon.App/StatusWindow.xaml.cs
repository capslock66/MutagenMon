using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
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

    /// <summary>Bound once to <c>SessionsGrid.ItemsSource</c> and updated
    /// in place on every refresh (<see cref="SyncRows"/>) rather than
    /// replaced — while the window stays open it's refreshed roughly once a
    /// second (FR-8.4), and rebinding a fresh list every tick would rebuild
    /// every row (losing selection/scroll position, and flickering) even
    /// when nothing actually changed.</summary>
    private readonly ObservableCollection<SessionSummaryRow> _sessionRows = new();
    private readonly ILogger _logger;

    public StatusWindow(ILogger logger)
    {
        InitializeComponent();
        SessionsGrid.ItemsSource = _sessionRows;
        _logger = logger;
    }

    public void UpdateContent(string title, ImageSource? icon, MonitorSnapshot snapshot, IReadOnlyList<string> sessionNames)
    {
        Title = "MutagenMon";
        TitleText.Text = StripAppNamePrefix(title);
        StatusIcon.Source = icon;

        var rows = StatusReportFormatter.BuildSessionRows(sessionNames, snapshot.SessionStatuses, snapshot.LastChangedUtc);
        SyncRows(_sessionRows, rows);

        ConflictsText.Text = StatusReportFormatter.BuildConflictsSection(sessionNames, snapshot.Conflicts);

        var hasUnresolvedConflicts = StatusReportFormatter.HasUnresolvedConflicts(snapshot.Conflicts);
        OkButton.Visibility = hasUnresolvedConflicts ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;
        ResolveConflictsButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>The incoming title is the tray tooltip, formatted as
    /// "&lt;TrayTooltip&gt;: &lt;status&gt;" (<see cref="TrayIconStateResolver"/>)
    /// — redundant here since the window's own title bar already says
    /// "MutagenMon". Strips generically on the first ": " rather than a
    /// hardcoded "MutagenMon:", since TrayTooltip is configurable.</summary>
    private static string StripAppNamePrefix(string title)
    {
        var separatorIndex = title.IndexOf(": ", StringComparison.Ordinal);
        return separatorIndex >= 0 ? title[(separatorIndex + 2)..] : title;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window OK clicked");
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
        Hide();
        ResolveConflictsRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

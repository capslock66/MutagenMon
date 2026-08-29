using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
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

    public StatusWindow()
    {
        InitializeComponent();
    }

    public void UpdateContent(string title, ImageSource? icon, MonitorSnapshot snapshot, IReadOnlyList<string> sessionNames)
    {
        Title = "MutagenMon";
        TitleText.Text = StripAppNamePrefix(title);
        StatusIcon.Source = icon;

        var body = StatusReportFormatter.BuildSessionsSection(sessionNames, snapshot.SessionStatuses);
        var conflictsSection = StatusReportFormatter.BuildConflictsSection(sessionNames, snapshot.Conflicts);
        if (conflictsSection.Length > 0)
            body += "\n\n" + conflictsSection;
        BodyText.Text = body;

        var hasUnresolvedConflicts = StatusReportFormatter.HasUnresolvedConflicts(snapshot.Conflicts);
        OkButton.Visibility = hasUnresolvedConflicts ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;
        ResolveConflictsButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The incoming title is the tray tooltip, formatted as
    /// "&lt;TRAY_TOOLTIP&gt;: &lt;status&gt;" (<see cref="TrayIconStateResolver"/>)
    /// — redundant here since the window's own title bar already says
    /// "MutagenMon". Strips generically on the first ": " rather than a
    /// hardcoded "MutagenMon:", since TRAY_TOOLTIP is configurable.</summary>
    private static string StripAppNamePrefix(string title)
    {
        var separatorIndex = title.IndexOf(": ", StringComparison.Ordinal);
        return separatorIndex >= 0 ? title[(separatorIndex + 2)..] : title;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Hide();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Hide();

    private void OnResolveConflictsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ResolveConflictsRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

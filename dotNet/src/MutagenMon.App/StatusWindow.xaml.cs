using System.ComponentModel;
using System.Windows;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Status;

namespace MutagenMon.App;

/// <summary>
/// Ports mutagenmonlib/wx/icon.py: TaskBarIcon.on_left_down() (FR-8) — the
/// full session status view, with a conflicts section and "Resolve
/// conflicts" action when at least one conflict is not auto-resolved
/// (FR-8.1/FR-8.2), or a plain informational OK dialog otherwise (FR-8.3).
/// Closing hides rather than closes the window, so re-opening it doesn't
/// reconstruct it from scratch — matches NFR-7's "no main window, dialogs
/// on demand" model.
/// </summary>
public partial class StatusWindow : Window
{
    public StatusWindow()
    {
        InitializeComponent();
    }

    public void UpdateContent(string title, MonitorSnapshot snapshot, IReadOnlyList<string> sessionNames)
    {
        Title = title;
        TitleText.Text = title;

        var body = StatusReportFormatter.BuildSessionsSection(sessionNames, snapshot.SessionStatuses);
        var conflictsSection = StatusReportFormatter.BuildConflictsSection(sessionNames, snapshot.Conflicts);
        if (conflictsSection.Length > 0)
        {
            body += "\n\n" + conflictsSection;
        }
        BodyText.Text = body;

        var hasUnresolvedConflicts = StatusReportFormatter.HasUnresolvedConflicts(snapshot.Conflicts);
        OkButton.Visibility = hasUnresolvedConflicts ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;
        ResolveConflictsButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Hide();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Hide();

    private void OnResolveConflictsClick(object sender, RoutedEventArgs e)
    {
        // FR-9 (manual conflict resolution) is a later phase — see
        // requirements/05-wpf-migration-notes.md §6 Phase 3.
        GenericMessageDialog.ShowInfo(this, "MutagenMon", "Conflict resolution isn't implemented yet.");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
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
        Hide();
        ResolveConflictsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deliberate test button — throws the exact exception
    /// type/message hit in production by the FR-9 SSH-endpoint
    /// misclassification bug (an embedded ':' in a path segment is invalid
    /// NTFS syntax: IOException "The filename, directory name, or volume
    /// label syntax is incorrect."), on a background thread awaited from an
    /// async void handler — the same propagation path the real bug went
    /// through. Lets the unhandled-exception logging
    /// (App.xaml.cs: OnDispatcherUnhandledException /
    /// OnDispatcherUnhandledExceptionFilter) be re-verified on demand
    /// without needing a real mutagen conflict.</summary>
    private async void OnBoomClick(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => _ = new FileInfo(@"C:\boom:test.txt").Length);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

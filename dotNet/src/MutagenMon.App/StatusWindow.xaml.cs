using System.ComponentModel;
using System.Windows;

namespace MutagenMon.App;

/// <summary>Placeholder for the real FR-8 status view (Phase 2). Closing
/// hides rather than closes the window, so re-opening it doesn't
/// reconstruct it from scratch — matches NFR-7's "no main window, dialogs
/// on demand" model.</summary>
public partial class StatusWindow : Window
{
    public StatusWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

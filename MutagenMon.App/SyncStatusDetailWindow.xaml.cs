using System.Windows;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>
/// Non-modal, resizable, scrollable popup showing the raw output of
/// `mutagen sync list -l &lt;name&gt;` for a single session (FR-28). Unlike
/// <see cref="GenericMessageDialog"/> (fixed width, no scrolling, plain
/// wrapping text) this content is a multi-line fixed-width CLI dump that
/// can easily exceed a small dialog, so it needs a resizable window with a
/// monospace, scrollable, selectable (read-only <c>TextBox</c>, not
/// <c>TextBlock</c>) body. Non-modal (shown via <see cref="Window.Show"/>,
/// not <see cref="Window.ShowDialog"/>) so the status view and other
/// windows stay usable while it's open — the Close button therefore
/// doesn't set <c>IsCancel</c>, which would try to set
/// <see cref="Window.DialogResult"/> and throw on a non-dialog window.
/// The caller (App.xaml.cs, which owns <c>MutagenCliClient</c>) re-runs
/// the command on <see cref="RefreshRequested"/> and pushes the new text
/// back in via <see cref="SetStatusText"/>, rather than this window calling
/// the CLI itself.
/// </summary>
public partial class SyncStatusDetailWindow : Window
{
    private readonly ILogger _logger;

    /// <summary>Raised when the user clicks Refresh (FR-28.6) — the caller
    /// re-runs the FR-28.2 command and calls <see cref="SetStatusText"/>
    /// with the new output.</summary>
    public event EventHandler? RefreshRequested;

    private readonly string _sessionName;

    public SyncStatusDetailWindow(ILogger logger, string sessionName)
    {
        InitializeComponent();
        _logger = logger;
        _sessionName = sessionName;
        Title = $"MutagenMon: sync status - {sessionName}";
    }

    /// <summary>Updates the popup's body and stamps the title with the
    /// refresh's timestamp (FR-28.8), e.g. "MutagenMon: sync status -
    /// t1 (20260910 - 17:23:45)" — called after both the initial fetch and
    /// every subsequent Refresh, so the title always reflects when the
    /// currently-displayed content was actually retrieved.</summary>
    public void SetStatusText(string statusText)
    {
        BodyText.Text = statusText;
        Title = $"MutagenMon: sync status - {_sessionName} ({DateTime.Now:yyyy-MM-dd - HH:mm:ss})";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: {Title} Refresh clicked", Title);
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: {Title} Close clicked", Title);
        Close();
    }
}

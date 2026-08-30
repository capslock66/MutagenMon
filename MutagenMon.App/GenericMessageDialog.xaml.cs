using System.Windows;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>
/// Reusable title/body/OK[/Cancel] window — per
/// requirements/05-wpf-migration-notes.md §4, replaces the legacy's
/// repeated <c>wx.MessageDialog(OK[/Cancel])</c> pattern (screens 3/4/7/8/9)
/// with a single WPF window instead of one dialog type per call site.
/// </summary>
public partial class GenericMessageDialog : Window
{
    private ILogger _logger = null!;

    public GenericMessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows an OK-only informational dialog and blocks until dismissed.</summary>
    public static void ShowInfo(Window? owner, ILogger logger, string title, string body)
    {
        var dialog = new GenericMessageDialog { Title = title, Owner = owner, _logger = logger };
        dialog.BodyText.Text = body;
        dialog.ShowDialog();
    }

    /// <summary>Shows an OK/Cancel confirmation dialog and returns true if OK was chosen.</summary>
    public static bool ShowConfirm(Window? owner, ILogger logger, string title, string body, string okLabel = "OK", string cancelLabel = "Cancel")
    {
        var dialog = new GenericMessageDialog { Title = title, Owner = owner, _logger = logger };
        dialog.BodyText.Text = body;
        dialog.OkButton.Content = okLabel;
        dialog.CancelButton.Content = cancelLabel;
        dialog.CancelButton.Visibility = Visibility.Visible;
        return dialog.ShowDialog() == true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: {Title} OK clicked", Title);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: {Title} Cancel clicked", Title);
        DialogResult = false;
    }
}

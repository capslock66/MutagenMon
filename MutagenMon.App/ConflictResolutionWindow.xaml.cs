using System.Windows;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>
/// Implements the single-conflict resolution dialog
/// (FR-9.1/FR-9.2/FR-9.3) — one conflict at a time,
/// numbered "N of total", with the file path, an A/B comparison, and a
/// Visual merge / A wins / B wins choice pre-selected by
/// <see cref="ConflictBatchPlanner.DefaultChoice"/>.
/// </summary>
public partial class ConflictResolutionWindow : Window
{
    private ConflictResolutionChoice _result;
    private ILogger _logger = null!;

    public ConflictResolutionWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows the dialog and returns the chosen resolution, or null
    /// if the user cancelled — cancelling here MUST abort the whole batch
    /// (FR-9.4), which is the caller's responsibility, not this window's.</summary>
    public static ConflictResolutionChoice? Show(
        Window? owner, ILogger logger, int count, int total, string fileName,
        string alphaUrl, FileStat alphaStat, string betaUrl, FileStat betaStat,
        ConflictResolutionChoice defaultChoice)
    {
        var dialog = new ConflictResolutionWindow
        {
            Owner = owner,
            Title = $"MutagenMon: resolve file conflict {count} of {total}",
            _logger = logger,
        };
        dialog.FileNameText.Text = fileName;
        dialog.AlphaInfoText.Text = FormatSide("A", alphaUrl, alphaStat);
        dialog.BetaInfoText.Text = FormatSide("B", betaUrl, betaStat);

        // Visual merge needs two actual files to diff — not applicable to a
        // directory-level conflict, or when one side no longer exists.
        var allowVisualMerge = !alphaStat.IsDirectory && !betaStat.IsDirectory && alphaStat.Exists && betaStat.Exists;
        dialog.VisualMergeOption.IsEnabled = allowVisualMerge;

        if (defaultChoice == ConflictResolutionChoice.AWins)
            dialog.AWinsOption.IsChecked = true;
        else
            dialog.BWinsOption.IsChecked = true;

        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private static string FormatSide(string label, string url, FileStat stat)
    {
        if (!stat.Exists)
            return $"{label}: {url}\n(does not exist)";
        if (stat.IsDirectory)
            return $"{label}: {url}\n(directory) last modified {stat.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        return $"{label}: {url}\n{stat.SizeBytes} bytes, {stat.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _result = VisualMergeOption.IsChecked == true
            ? ConflictResolutionChoice.VisualMerge
            : AWinsOption.IsChecked == true
                ? ConflictResolutionChoice.AWins
                : ConflictResolutionChoice.BWins;
        _logger.LogInformation("User action: conflict resolution OK clicked ({Choice})", _result);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: conflict resolution Cancel clicked");
        DialogResult = false;
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>
/// Implements the manual conflict resolution dialog (FR-9): a grid of every
/// pending conflict (Alpha, Beta, Resolution). Selecting an unresolved row
/// shows, to the right of the grid, the Visual merge / Alpha wins / Beta
/// wins options and an OK button; the Resolution column follows the pick,
/// and OK applies it. Once applied, the options disappear until another row
/// is selected, and a resolved row never offers them again.
/// </summary>
public partial class ConflictResolutionWindow : Window
{
    private readonly ObservableCollection<ConflictItem> _items = new();
    private readonly ConflictResolutionService _service;
    private readonly ILogger _logger;
    private ConflictItem? _current;
    private int _selectionVersion;
    private bool _suppressChoiceEvents;

    private ConflictResolutionWindow(ConflictResolutionService service, ILogger logger, IReadOnlyList<ConflictItem> items)
    {
        InitializeComponent();
        _service = service;
        _logger = logger;
        foreach (var item in items)
            _items.Add(item);
        ConflictsGrid.ItemsSource = _items;
        StatusText.Text = "Select a conflict.";
    }

    /// <summary>Shows the dialog modally; returns once the user closes it.</summary>
    public static void Show(
        Window? owner, ILogger logger, ConflictResolutionService service, IReadOnlyList<ConflictItem> items)
    {
        var dialog = new ConflictResolutionWindow(service, logger, items) { Owner = owner };
        dialog.ShowDialog();
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = null;
        OptionsPanel.Visibility = Visibility.Collapsed;
        AlphaInfoText.Text = "";
        BetaInfoText.Text = "";
        DescriptionText.Text = "";
        DetailsText.Text = "";
        var version = ++_selectionVersion;

        if (ConflictsGrid.SelectedItem is not ConflictItem item)
        {
            StatusText.Text = "Select a conflict.";
            return;
        }

        // The grid truncates long descriptions; the full text goes here.
        DescriptionText.Text = item.Description;
        DetailsText.Text = item.Details;

        if (item.IsResolved)
        {
            ShowSides(item);
            StatusText.Text = item.Resolution;
            return;
        }

        StatusText.Text = "";

        try
        {
            if (item.AlphaStat is null || item.BetaStat is null)
            {
                var conflict = item.Conflict;
                var (alpha, beta) = await RunBusyAsync(() =>
                    ConnectingIndicatorWindow.RunAsync(this, async () =>
                    {
                        var a = await _service.StatAlphaAsync(conflict, CancellationToken.None);
                        var b = await _service.StatBetaAsync(conflict, CancellationToken.None);
                        return (a, b);
                    }));
                item.AlphaStat = alpha;
                item.BetaStat = beta;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stat conflict {FileName} ({SessionName})", item.Conflict.FileName, item.SessionName);
            item.MarkFailed(ex.Message);
            return;
        }

        // The user moved to another row while the stat was running.
        if (version != _selectionVersion)
            return;

        ShowChoices(item);
    }

    private void ShowChoices(ConflictItem item)
    {
        var alphaStat = item.AlphaStat!;
        var betaStat = item.BetaStat!;
        _current = item;

        ShowSides(item);

        // Visual merge needs two actual files to diff — not applicable to a
        // directory-level conflict, or when one side no longer exists.
        VisualMergeOption.IsEnabled = !alphaStat.IsDirectory && !betaStat.IsDirectory && alphaStat.Exists && betaStat.Exists;

        // FR-9.3: prefer the more recently modified side, "Beta wins" on a tie.
        var choice = item.Choice ?? (alphaStat.ModifiedUtc > betaStat.ModifiedUtc
            ? ConflictResolutionChoice.AWins
            : ConflictResolutionChoice.BWins);

        _suppressChoiceEvents = true;
        VisualMergeOption.IsChecked = choice == ConflictResolutionChoice.VisualMerge;
        AWinsOption.IsChecked = choice == ConflictResolutionChoice.AWins;
        BWinsOption.IsChecked = choice == ConflictResolutionChoice.BWins;
        _suppressChoiceEvents = false;

        item.SetPending(choice);
        OptionsPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Fills the side panel's Alpha/Beta details, when the row's
    /// stats have been fetched (always the case once it was selected
    /// unresolved).</summary>
    private void ShowSides(ConflictItem item)
    {
        if (item.AlphaStat is null || item.BetaStat is null)
            return;

        AlphaInfoText.Text = FormatSide("Alpha", item.Conflict.Alpha.Url, item.AlphaStat);
        BetaInfoText.Text = FormatSide("Beta", item.Conflict.Beta.Url, item.BetaStat);
    }

    private void OnChoiceChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressChoiceEvents || _current is null)
            return;

        _current.SetPending(SelectedChoice());
    }

    private ConflictResolutionChoice SelectedChoice() =>
        VisualMergeOption.IsChecked == true
            ? ConflictResolutionChoice.VisualMerge
            : AWinsOption.IsChecked == true
                ? ConflictResolutionChoice.AWins
                : ConflictResolutionChoice.BWins;

    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsResolved: false } item)
            return;

        var choice = SelectedChoice();
        _logger.LogInformation(
            "User action: conflict resolution OK clicked ({Choice}, session={SessionName}, file={FileName})",
            choice, item.SessionName, item.Conflict.FileName);

        try
        {
            var applied = await RunBusyAsync(() => ApplyAsync(item.Conflict, choice));
            if (!applied)
            {
                GenericMessageDialog.ShowInfo(
                    this, _logger, "MutagenMon: resolve file conflict",
                    $"No change was made in the merge tool; the conflict is still unresolved:\n\n{item.Conflict.FileName}");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve conflict {FileName} ({SessionName})", item.Conflict.FileName, item.SessionName);
            item.MarkFailed(ex.Message);
            return;
        }

        item.MarkResolved(choice);
        // The options only come back once another row is selected.
        _current = null;
        OptionsPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = item.Resolution;
    }

    /// <summary>Applies <paramref name="choice"/>; returns false only for a
    /// visual merge in which neither side was modified.</summary>
    private async Task<bool> ApplyAsync(PendingConflict conflict, ConflictResolutionChoice choice)
    {
        if (choice != ConflictResolutionChoice.VisualMerge)
        {
            await ConnectingIndicatorWindow.RunAsync(
                this, () => _service.ResolveAsync(conflict, choice, DateTimeOffset.UtcNow, CancellationToken.None));
            return true;
        }

        var preparation = await ConnectingIndicatorWindow.RunAsync(
            this, () => _service.PrepareVisualMergeAsync(conflict, CancellationToken.None));

        await _service.RunMergeToolAsync(preparation, CancellationToken.None);

        return await ConnectingIndicatorWindow.RunAsync(
            this, () => _service.CompleteVisualMergeAsync(conflict, preparation, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    /// <summary>Blocks input to the whole dialog while a (possibly remote)
    /// operation or the external merge tool is running, so the selection
    /// can't move underneath it.</summary>
    private async Task<T> RunBusyAsync<T>(Func<Task<T>> operation)
    {
        RootGrid.IsEnabled = false;
        try
        {
            return await operation();
        }
        finally
        {
            RootGrid.IsEnabled = true;
        }
    }

    private static string FormatSide(string label, string url, FileStat stat)
    {
        if (!stat.Exists)
            return $"{label}: {url}\n(does not exist)";
        if (stat.IsDirectory)
            return $"{label}: {url}\n(directory) last modified {stat.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        return $"{label}: {url}\n{stat.SizeBytes} bytes, {stat.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: conflict resolution Close clicked");
        Close();
    }
}

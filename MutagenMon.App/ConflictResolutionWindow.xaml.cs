using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>
/// Implements the manual conflict resolution dialog (FR-9): a grid of every
/// pending conflict whose Resolution cell offers Alpha wins / Beta wins
/// (preselected from the modification dates) and Visual merge. Picking Visual
/// merge launches the merge tool at once; nothing is propagated before OK,
/// which applies every pick. Cancel (or closing) changes nothing.
/// </summary>
public partial class ConflictResolutionWindow : Window
{
    private readonly ObservableCollection<ConflictItem> _items = new();
    private readonly ConflictResolutionService _service;
    private readonly ILogger _logger;

    private bool _isClosed;

    private ConflictResolutionWindow(ConflictResolutionService service, ILogger logger, IReadOnlyList<ConflictItem> items)
    {
        InitializeComponent();
        Closed += (_, _) => _isClosed = true;
        _service = service;
        _logger = logger;
        foreach (var item in items)
        {
            item.ChoiceChangedByUser += OnItemChoiceChanged;
            _items.Add(item);
        }

        ConflictsGrid.ItemsSource = _items;
        OkButton.IsEnabled = false;
        StatusText.Text = "Loading conflicts...";
    }

    /// <summary>Shows the dialog modally; returns once the user closes it.</summary>
    public static void Show(
        Window? owner, ILogger logger, ConflictResolutionService service, IReadOnlyList<ConflictItem> items)
    {
        var dialog = new ConflictResolutionWindow(service, logger, items) { Owner = owner };
        dialog.ShowDialog();
    }

    /// <summary>Fetches both sides' state of every conflict (a stat can mean an SSH round trip), which gives
    /// each row its default choice from the dates.</summary>
    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        await RunBusyAsync(() => ConnectingIndicatorWindow.RunAsync(this, async () =>
        {
            foreach (var item in _items)
                await StatItemAsync(item);
            return true;
        }));

        OkButton.IsEnabled = _items.Any(item_ => item_.CanEdit);
        OnSelectionChanged(this, null!);
    }

    private async Task StatItemAsync(ConflictItem item)
    {
        try
        {
            var alpha = await _service.StatAlphaAsync(item.Conflict, CancellationToken.None);
            var beta = await _service.StatBetaAsync(item.Conflict, CancellationToken.None);
            item.SetStats(alpha, beta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stat conflict {FileName} ({SessionName})", item.Conflict.FileName, item.SessionName);
            item.MarkFailed(ex.Message);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AlphaInfoText.Text = "";
        BetaInfoText.Text = "";
        DescriptionText.Text = "";
        DetailsText.Text = "";

        if (ConflictsGrid.SelectedItem is not ConflictItem item)
        {
            StatusText.Text = "Select a conflict.";
            return;
        }

        StatusText.Text = item.StatusText;
        // The grid truncates long descriptions; the full text goes here.
        DescriptionText.Text = item.Description;
        DetailsText.Text = item.Details;

        if (item.AlphaStat is null || item.BetaStat is null)
            return;

        AlphaInfoText.Text = FormatSide("Alpha", item.Conflict.Alpha.Url, item.AlphaStat);
        BetaInfoText.Text = FormatSide("Beta", item.Conflict.Beta.Url, item.BetaStat);
    }

    /// <summary>Forwards the user's pick to the row. Done here rather than through a two-way binding, whose setter
    /// would run the merge tool inside the binding engine (which swallows errors raised there).</summary>
    private void OnChoiceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: ConflictItem item } && e.AddedItems.Count == 1 && e.AddedItems[0] is string text)
            item.SelectedChoiceText = text;
    }

    /// <summary>The user picked another option in a Resolution cell. Switching between the two "wins" only
    /// records the pick (applied by OK); Visual merge runs the merge tool now.</summary>
    private async void OnItemChoiceChanged(object? sender, ConflictResolutionChoice choice)
    {
        if (sender is not ConflictItem item)
            return;

        _logger.LogInformation(
            "User action: conflict resolution choice changed ({Choice}, session={SessionName}, file={FileName})",
            choice, item.SessionName, item.Conflict.FileName);

        if (choice != ConflictResolutionChoice.VisualMerge)
        {
            item.MergePreparation = null;
            return;
        }

        var previous = item.PreviousChoice ?? DefaultChoice(item);
        try
        {
            var preparation = await RunBusyAsync(() => RunMergeToolAsync(item));
            if (preparation is null)
            {
                item.MergePreparation = null;
                item.RestoreChoice(previous);
                GenericMessageDialog.ShowInfo(
                    this, _logger, "MutagenMon: resolve file conflict",
                    $"No change was made in the merge tool; the conflict keeps its previous resolution:\n\n{item.Conflict.FileName}");
                return;
            }

            item.MergePreparation = preparation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run the merge tool for {FileName} ({SessionName})", item.Conflict.FileName, item.SessionName);
            item.MarkFailed(ex.Message);
        }
    }

    /// <summary>FR-9.3: the more recently modified side wins, "Beta wins" on a tie.</summary>
    private static ConflictResolutionChoice DefaultChoice(ConflictItem item) =>
        item.AlphaStat!.ModifiedUtc > item.BetaStat!.ModifiedUtc ? ConflictResolutionChoice.AWins : ConflictResolutionChoice.BWins;

    /// <summary>Fetches the local copies, runs the merge tool and returns the preparation, or null when the tool
    /// modified neither side.</summary>
    private async Task<VisualMergePreparation?> RunMergeToolAsync(ConflictItem item)
    {
        var preparation = await ConnectingIndicatorWindow.RunAsync(
            this, () => _service.PrepareVisualMergeAsync(item.Conflict, CancellationToken.None));
        await _service.RunMergeToolAsync(preparation, CancellationToken.None);
        return ConflictResolutionService.IsMergeModified(preparation) ? preparation : null;
    }

    /// <summary>Applies every pick, in grid order. A failing row shows its error and stays unresolved; the dialog
    /// then stays open, otherwise it closes.</summary>
    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: conflict resolution OK clicked");
        await RunBusyAsync(async () =>
        {
            foreach (var item in _items.Where(item_ => item_.CanEdit))
                await ApplyItemAsync(item);
            return true;
        });

        // The window may have been closed with its title bar while the choices were applied.
        if (!_isClosed && !_items.Any(item_ => item_.HasError))
            Close();

        OkButton.IsEnabled = _items.Any(item_ => item_.CanEdit);
        OnSelectionChanged(this, null!);
    }

    private async Task<bool> ApplyItemAsync(ConflictItem item)
    {
        var choice = item.Choice!.Value;
        try
        {
            if (choice == ConflictResolutionChoice.VisualMerge)
            {
                var applied = await ConnectingIndicatorWindow.RunAsync(this, () => _service.CompleteVisualMergeAsync(
                    item.Conflict, item.MergePreparation!, DateTimeOffset.UtcNow, CancellationToken.None));
                if (!applied)
                    throw new InvalidOperationException("The merged file was not modified.");
            }
            else
            {
                await ConnectingIndicatorWindow.RunAsync(
                    this, () => _service.ResolveAsync(item.Conflict, choice, DateTimeOffset.UtcNow, CancellationToken.None));
            }

            item.MarkResolved();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve conflict {FileName} ({SessionName})", item.Conflict.FileName, item.SessionName);
            item.MarkFailed(ex.Message);
            return false;
        }
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

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: conflict resolution Cancel clicked");
        Close();
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>
/// Real-time view of the last 100 logged events — no file re-read/reload:
/// populated once from <see cref="FileLoggerProvider.GetRecentEntries"/>
/// (so it isn't empty just because the tab wasn't open yet), then kept live
/// via <see cref="FileLoggerProvider.EntryLogged"/>. "Clear" only empties
/// this tab's own collection, never the log file itself or the provider's
/// backlog — new entries keep arriving afterwards exactly as before.
/// Master/detail (FR-43): the grid is the master; selecting a row shows its
/// full message, word-wrapped, in the resizable detail panel below the
/// splitter. Error-level rows are highlighted in red (FR-44). "Generate
/// exception" (FR-45) is only visible when <c>ShowGenerateException</c> is
/// enabled in the mutagen monitor configuration.
/// Auto-scroll (FR-46): the grid "sticks" to its last row as new entries
/// arrive, as long as <see cref="_autoScroll"/> is true. Selecting a row
/// pins the view (so it isn't yanked out from under whatever the user is
/// reading); scrolling back down to the bottom un-pins it. <see cref="_suppressScrollChanged"/>
/// guards against our own programmatic <c>ScrollIntoView</c> calls being
/// misread as the user scrolling.
/// </summary>
public partial class LogsView : UserControl
{
    private const int MaxRows = 100;
    private const double AtBottomTolerance = 2.0;

    private ILogger _logger = null!;
    private FileLoggerProvider _loggerProvider = null!;
    private readonly ObservableCollection<LogEntry> _entries = new();
    private bool _autoScroll = true;
    private bool _suppressScrollChanged;
    private ScrollViewer? _gridScrollViewer;

    public LogsView()
    {
        InitializeComponent();
        LogGrid.ItemsSource = _entries;
        LogGrid.Loaded += OnLogGridLoaded;
    }

    public void Initialize(ILogger logger, FileLoggerProvider loggerProvider, bool showGenerateException)
    {
        _logger = logger;
        _loggerProvider = loggerProvider;
        GenerateExceptionButton.Visibility = showGenerateException ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in loggerProvider.GetRecentEntries())
            _entries.Add(entry);

        loggerProvider.EntryLogged += OnEntryLogged;
    }

    /// <summary>Raised on whatever thread produced the log entry — never the
    /// UI thread in general (most logging here happens from the background
    /// poller), so every touch of <see cref="_entries"/> must be marshaled.</summary>
    private void OnEntryLogged(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _entries.Add(entry);
            while (_entries.Count > MaxRows)
                _entries.RemoveAt(0);

            if (_autoScroll)
                ScrollToLastEntry();
        });
    }

    /// <summary>Finds the grid's internal <see cref="ScrollViewer"/> (not
    /// available until the template is applied) so its position can be
    /// watched, and performs the initial scroll-to-bottom — covers both the
    /// tab being shown for the first time after <see cref="Initialize"/>
    /// already populated it, and (defensively) any later re-load.</summary>
    private void OnLogGridLoaded(object sender, RoutedEventArgs e)
    {
        if (_gridScrollViewer is null)
        {
            _gridScrollViewer = FindVisualChild<ScrollViewer>(LogGrid);
            if (_gridScrollViewer is not null)
                _gridScrollViewer.ScrollChanged += OnGridScrollChanged;
        }

        if (_autoScroll)
            ScrollToLastEntry();
    }

    /// <summary>Tracks whether the user is at the bottom of the grid, so a
    /// later scroll back down re-arms auto-scroll — the only way back in,
    /// short of "Clear". Ignored while <see cref="_suppressScrollChanged"/>
    /// is set, i.e. for the scroll our own <see cref="ScrollToLastEntry"/>
    /// just caused.</summary>
    private void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollChanged)
            return;
        _autoScroll = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - AtBottomTolerance;
    }

    private void ScrollToLastEntry()
    {
        if (_entries.Count == 0)
            return;
        _suppressScrollChanged = true;
        LogGrid.ScrollIntoView(_entries[^1]);
        // The layout pass that ScrollChanged reacts to can land after this
        // call returns, so the flag is cleared on a later dispatcher pass
        // rather than immediately.
        Dispatcher.BeginInvoke(() => _suppressScrollChanged = false, DispatcherPriority.Background);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;
            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: logs tab Clear clicked");
        _entries.Clear();
        DetailText.Text = "";
        _autoScroll = true;
    }

    /// <summary>FR-43: shows the selected row's full message (including any
    /// appended exception text — see <see cref="FileLoggerProvider"/>'s
    /// <c>FormatMessage</c>) in the word-wrapped detail panel below the
    /// grid. FR-46: selecting a row also pins the view (see the class
    /// remarks) so it isn't scrolled away from under the user.</summary>
    private void OnLogGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogGrid.SelectedItem is LogEntry entry)
        {
            DetailText.Text = entry.Message;
            _autoScroll = false;
        }
        else
        {
            DetailText.Text = "";
        }
    }

    /// <summary>FR-45: deliberately throws, unhandled, so it reaches
    /// App.xaml.cs's <c>OnDispatcherUnhandledException</c> — the same path a
    /// real UI-thread crash takes (FR-14.1) — to test that path on demand.
    /// Only reachable when <c>ShowGenerateException</c> is enabled.</summary>
    private void OnGenerateExceptionClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: logs tab Generate exception clicked");
        throw new InvalidOperationException(
            "Test exception generated via the Logs tab \"Generate exception\" button (ShowGenerateException).");
    }

    private void OnOpenLogFileClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: logs tab Open log file clicked");
        var path = _loggerProvider.PrimaryLogPath;
        if (path is null || !File.Exists(path))
        {
            MessageBox.Show(
                Window.GetWindow(this), "The log file does not exist yet.",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log file '{Path}'", path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not open the log file:\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClearLogFileClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: logs tab Clear log file clicked");
        var path = _loggerProvider.PrimaryLogPath;
        if (path is null)
        {
            MessageBox.Show(
                Window.GetWindow(this), "The log file path is not known yet.",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!GenericMessageDialog.ShowConfirm(
                Window.GetWindow(this), _logger, "MutagenMon — clear log file",
                $"Clear the log file on disk?\n\n{path}\n\nThis cannot be undone.", okLabel: "Yes", cancelLabel: "No"))
            return;

        try
        {
            File.WriteAllText(path, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear log file '{Path}'", path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not clear the log file:\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

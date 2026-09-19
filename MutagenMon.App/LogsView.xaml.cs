using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>
/// Real-time view of the last 100 logged events — no file re-read/reload:
/// populated once from <see cref="FileLoggerProvider.GetRecentEntries"/>
/// (so it isn't empty just because the tab wasn't open yet), then kept live
/// via <see cref="FileLoggerProvider.EntryLogged"/>. "Clear" only empties
/// this tab's own collection, never the log file itself or the provider's
/// backlog — new entries keep arriving afterwards exactly as before.
/// </summary>
public partial class LogsView : UserControl
{
    private const int MaxRows = 100;

    private ILogger _logger = null!;
    private FileLoggerProvider _loggerProvider = null!;
    private readonly ObservableCollection<LogEntry> _entries = new();

    public LogsView()
    {
        InitializeComponent();
        LogGrid.ItemsSource = _entries;
    }

    public void Initialize(ILogger logger, FileLoggerProvider loggerProvider)
    {
        _logger = logger;
        _loggerProvider = loggerProvider;

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
        });
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: logs tab Clear clicked");
        _entries.Clear();
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

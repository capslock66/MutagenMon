using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Scripts;

namespace MutagenMon.App;

/// <summary>
/// The Scripts tab (FR-47): lists the scripts of the scripts file, shows the
/// selected one and runs it on its remote host through
/// <see cref="ScriptRunner"/>. The file is read when the tab is first shown
/// and on Reload — never watched (FR-49.1). A running script keeps the
/// <see cref="ScriptDefinition"/> it was started with, so a Reload does not
/// affect it (FR-49.3). Runner output arrives on background threads and is
/// marshaled to the UI thread (FR-52.2). Shares no state with polling or the
/// tray icon (NFR-1).
/// </summary>
public partial class ScriptsView : UserControl
{
    private const int MaxOutputLines = 5000;

    private ILogger _logger = null!;
    private Func<string> _scriptsPathProvider = null!;
    private Func<ScriptRunner> _runnerFactory = null!;
    private readonly ObservableCollection<ScriptListItem> _items = new();
    private CancellationTokenSource? _runSource;
    private bool _isLoaded;
    private bool _isRunning;

    public ScriptsView()
    {
        InitializeComponent();
        ScriptList.ItemsSource = _items;
        Loaded += OnViewLoaded;
    }

    /// <param name="scriptsPathProvider">Resolves the scripts file path at
    /// call time, so a config reload is picked up.</param>
    /// <param name="runnerFactory">Builds a runner from the current options
    /// (the runner reads ssh path and timeout once, at construction).</param>
    public void Initialize(ILogger logger, Func<string> scriptsPathProvider, Func<ScriptRunner> runnerFactory)
    {
        _logger = logger;
        _scriptsPathProvider = scriptsPathProvider;
        _runnerFactory = runnerFactory;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
            return;

        _isLoaded = true;
        LoadScripts();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: Scripts Reload clicked");
        LoadScripts();
    }

    /// <summary>Rebuilds the list from the file (FR-49), keeping the
    /// selection when a script of the same name still exists.</summary>
    private void LoadScripts()
    {
        var path = _scriptsPathProvider();
        // The path can change with a config reload, so refresh the link on every load.
        OpenFileLink.Inlines.Clear();
        OpenFileLink.Inlines.Add(path);
        var previousName = (ScriptList.SelectedItem as ScriptListItem)?.Script.Name;

        IReadOnlyList<ScriptDefinition> scripts;
        try
        {
            scripts = ScriptFileParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Scripts file '{Path}' could not be read", path);
            _items.Clear();
            ShowInfo(ex is FileNotFoundException or DirectoryNotFoundException
                ? $"Scripts file not found: {path}"
                : $"Scripts file could not be read: {path} ({ex.Message})");
            return;
        }

        _logger.LogInformation("Loaded {Count} script(s) from '{Path}'", scripts.Count, path);
        InfoText.Visibility = Visibility.Collapsed;
        _items.Clear();
        foreach (var script in scripts)
            _items.Add(new ScriptListItem(script));

        ScriptList.SelectedItem = _items.FirstOrDefault(item => item.Script.Name == previousName);
        UpdateButtons();
    }

    private void OnOpenFileLinkClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: scripts file link clicked");
        var path = _scriptsPathProvider();

        if (!File.Exists(path))
        {
            MessageBox.Show(
                Window.GetWindow(this), "The scripts file does not exist.",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open scripts file '{Path}'", path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not open the scripts file:\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowInfo(string message)
    {
        InfoText.Text = message;
        InfoText.Visibility = Visibility.Visible;
        ScriptText.Text = "";
        UpdateButtons();
    }

    private void OnScriptListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScriptText.Text = (ScriptList.SelectedItem as ScriptListItem)?.Script.Body ?? "";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = ScriptList.SelectedItem as ScriptListItem;
        RunButton.IsEnabled = !_isRunning && selected is { Script.IsValid: true };
        CancelButton.IsEnabled = _isRunning;
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_isRunning || ScriptList.SelectedItem is not ScriptListItem { Script: { IsValid: true } script })
            return;

        _logger.LogInformation("User action: Scripts Run clicked for '{Script}'", script.Name);
        if (script.Confirm
            && !GenericMessageDialog.ShowConfirm(
                Window.GetWindow(this), _logger, "Run script", $"Run script '{script.Name}' on {script.Host}?", "Run"))
            return;

        _isRunning = true;
        UpdateButtons();
        OutputBox.Document.Blocks.Clear();
        SetStatus($"Running '{script.Name}' on {script.Host}...", isError: false);

        _runSource = new CancellationTokenSource();
        try
        {
            var result = await _runnerFactory().RunAsync(
                script, line => Dispatcher.BeginInvoke(() => AppendOutputLine(line.Text, line.IsError)), _runSource.Token);
            SetStatus(DescribeOutcome(result), isError: result.Outcome != ScriptRunOutcome.Completed || result.ExitCode != 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script '{Script}' failed unexpectedly", script.Name);
            SetStatus($"Script run failed: {ex.Message}", isError: true);
        }
        finally
        {
            _runSource.Dispose();
            _runSource = null;
            _isRunning = false;
            UpdateButtons();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: Scripts Cancel clicked");
        _runSource?.Cancel();
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? Brushes.Red : SystemColors.ControlTextBrush;
    }

    /// <summary>The outcome of a run (FR-52.3); cancel and timeout state
    /// that server-side processes already detached keep running (FR-51.5).</summary>
    private static string DescribeOutcome(ScriptRunResult result)
    {
        const string DetachedNote = "The local ssh connection was closed; processes the script already detached on the server keep running.";
        var duration = $"{result.Duration.TotalSeconds:0.0}s";
        return result.Outcome switch
        {
            ScriptRunOutcome.Completed => $"Exit code {result.ExitCode} ({duration})",
            ScriptRunOutcome.Cancelled => $"Cancelled after {duration}. {DetachedNote}",
            ScriptRunOutcome.TimedOut => $"Timed out after {duration}. {DetachedNote}",
            _ => $"Failed to start ssh: {result.Error}",
        };
    }

    /// <summary>Appends one line, keeping only the last
    /// <see cref="MaxOutputLines"/> (FR-52.4). UI thread only.</summary>
    private void AppendOutputLine(string text, bool isError)
    {
        var paragraph = new Paragraph(new Run(text)) { Margin = new Thickness(0) };
        if (isError)
            paragraph.Foreground = Brushes.Red;

        var blocks = OutputBox.Document.Blocks;
        blocks.Add(paragraph);
        while (blocks.Count > MaxOutputLines)
            blocks.Remove(blocks.FirstBlock);

        OutputBox.ScrollToEnd();
    }
}

/// <summary>List row for one script: the name, and either the host and
/// description or, for an invalid script, the reason it cannot run
/// (FR-49.2).</summary>
public sealed record ScriptListItem(ScriptDefinition Script)
{
    public string Name => Script.Name;

    public bool IsInvalid => !Script.IsValid;

    public string Detail => Script.IsValid
        ? (Script.Description.Length == 0 ? Script.Host! : $"{Script.Host} — {Script.Description}")
        : $"Invalid: {Script.ValidationError}";
}

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
    /// <summary>Last run output and status per script name, so switching
    /// scripts and coming back shows that script's own last run. Also the
    /// sink of a run in progress: its output keeps accumulating here while
    /// another script is displayed.</summary>
    private readonly Dictionary<string, ScriptRunView> _runViews = new();
    private CancellationTokenSource? _runSource;
    /// <summary>Unsaved edits of scripts that are not the displayed one,
    /// by script name, so switching scripts does not lose them.</summary>
    private readonly Dictionary<string, string> _drafts = new();
    private bool _isLoaded;
    private bool _isRunning;
    private bool _isReloading;
    private bool _suppressTextChanged;

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
        if (HasUnsavedChanges())
        {
            if (!GenericMessageDialog.ShowConfirm(
                    Window.GetWindow(this), _logger, "Reload scripts",
                    "Some scripts have unsaved changes. Reloading discards them.", "Reload", "Cancel"))
                return;

            _drafts.Clear();
            SetScriptText((ScriptList.SelectedItem as ScriptListItem)?.Script.Body ?? "");
        }

        LoadScripts();
    }

    private bool HasUnsavedChanges() => _drafts.Count > 0 || IsDirty();

    /// <summary>True when the displayed text differs from the selected
    /// script's saved body (line endings and surrounding blank lines
    /// ignored, as the file format ignores them).</summary>
    private bool IsDirty() =>
        ScriptList.SelectedItem is ScriptListItem item && NormalizeBody(ScriptText.Text) != NormalizeBody(item.Script.Body);

    private static string NormalizeBody(string text) => text.Replace("\r\n", "\n").Trim('\n');

    private void SetScriptText(string text)
    {
        _suppressTextChanged = true;
        try
        {
            ScriptText.Text = text;
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private void OnScriptTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressTextChanged)
            UpdateButtons();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ScriptList.SelectedItem is not ScriptListItem item || !IsDirty())
            return;

        var name = item.Script.Name;
        var path = _scriptsPathProvider();
        _logger.LogInformation("User action: Scripts Save clicked for '{Script}'", name);
        try
        {
            ScriptFileEditor.ReplaceBody(path, name, ScriptText.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to save script '{Script}' to '{Path}'", name, path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not save the script '{name}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _logger.LogInformation("Script '{Script}' saved to '{Path}'", name, path);
        _drafts.Remove(name);
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
            _isReloading = true;
            try
            {
                _items.Clear();
            }
            finally
            {
                _isReloading = false;
            }
            ShowInfo(ex is FileNotFoundException or DirectoryNotFoundException
                ? $"Scripts file not found: {path}"
                : $"Scripts file could not be read: {path} ({ex.Message})");
            return;
        }

        _logger.LogInformation("Loaded {Count} script(s) from '{Path}'", scripts.Count, path);
        InfoText.Visibility = Visibility.Collapsed;
        // The rebuild below raises selection changes for items that no
        // longer exist: those must not be taken for the user switching scripts.
        _isReloading = true;
        try
        {
            _items.Clear();
            foreach (var script in scripts)
                _items.Add(new ScriptListItem(script));

            ScriptList.SelectedItem = _items.FirstOrDefault(item => item.Script.Name == previousName);
        }
        finally
        {
            _isReloading = false;
        }

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
        SetScriptText("");
        UpdateButtons();
    }

    private void OnScriptListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The text box still holds the previous script's text here: keep it as a draft if edited.
        if (!_isReloading && e.RemovedItems.Count > 0 && e.RemovedItems[0] is ScriptListItem previous)
        {
            if (NormalizeBody(ScriptText.Text) != NormalizeBody(previous.Script.Body))
                _drafts[previous.Script.Name] = ScriptText.Text;
            else
                _drafts.Remove(previous.Script.Name);
        }

        var selected = ScriptList.SelectedItem as ScriptListItem;
        var selectedName = selected?.Script.Name;
        SetScriptText(selectedName is not null && _drafts.TryGetValue(selectedName, out var draft) ? draft : selected?.Script.Body ?? "");
        ScriptText.IsReadOnly = selected is null;
        ShowRunView(selectedName);
        UpdateButtons();
    }

    /// <summary>Refreshes the output pane and status label from the cached
    /// run of <paramref name="scriptName"/> (blank when it has none).</summary>
    private void ShowRunView(string? scriptName)
    {
        OutputBox.Document.Blocks.Clear();
        if (scriptName is null || !_runViews.TryGetValue(scriptName, out var runView))
        {
            SetStatusText("", isError: false);
            return;
        }

        foreach (var line in runView.Lines)
            AppendOutputParagraph(line);
        SetStatusText(runView.StatusText, runView.StatusIsError);
    }

    private bool IsSelected(string scriptName) =>
        (ScriptList.SelectedItem as ScriptListItem)?.Script.Name == scriptName;

    private void UpdateButtons()
    {
        var selected = ScriptList.SelectedItem as ScriptListItem;
        var isDirty = IsDirty();
        SaveButton.IsEnabled = isDirty;
        // The run uses the saved script: running while the text differs from it would be misleading.
        RunButton.IsEnabled = !_isRunning && !isDirty && selected is { Script.IsValid: true };
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
        // A new run replaces the cached view of this script (FR-52.4: the pane is cleared at the start of each run).
        var runView = new ScriptRunView(script.Name);
        _runViews[script.Name] = runView;
        OutputBox.Document.Blocks.Clear();
        SetStatus(runView, $"Running '{script.Name}' on {script.Host}...", isError: false);

        _runSource = new CancellationTokenSource();
        try
        {
            var result = await _runnerFactory().RunAsync(
                script, line => Dispatcher.BeginInvoke(() => AppendOutput(runView, line)), _runSource.Token);
            SetStatus(runView, DescribeOutcome(result), isError: result.Outcome != ScriptRunOutcome.Completed || result.ExitCode != 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script '{Script}' failed unexpectedly", script.Name);
            SetStatus(runView, $"Script run failed: {ex.Message}", isError: true);
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

    /// <summary>Records the status in the cache and, when that script is the
    /// one displayed, in the label.</summary>
    private void SetStatus(ScriptRunView runView, string text, bool isError)
    {
        runView.StatusText = text;
        runView.StatusIsError = isError;
        if (IsSelected(runView.ScriptName))
            SetStatusText(text, isError);
    }

    private void SetStatusText(string text, bool isError)
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

    /// <summary>Caches one line (last <see cref="MaxOutputLines"/> only,
    /// FR-52.4) and shows it when that script is the one displayed. UI
    /// thread only.</summary>
    private void AppendOutput(ScriptRunView runView, ScriptOutputLine line)
    {
        runView.Lines.Enqueue(line);
        while (runView.Lines.Count > MaxOutputLines)
            runView.Lines.Dequeue();

        if (IsSelected(runView.ScriptName))
            AppendOutputParagraph(line);
    }

    /// <summary>Adds one line to the pane, keeping only the last
    /// <see cref="MaxOutputLines"/>. UI thread only.</summary>
    private void AppendOutputParagraph(ScriptOutputLine line)
    {
        var paragraph = new Paragraph(new Run(line.Text)) { Margin = new Thickness(0) };
        if (line.IsError)
            paragraph.Foreground = Brushes.Red;

        var blocks = OutputBox.Document.Blocks;
        blocks.Add(paragraph);
        while (blocks.Count > MaxOutputLines)
            blocks.Remove(blocks.FirstBlock);

        OutputBox.ScrollToEnd();
    }
}

/// <summary>Output and status of the last run of one script (FR-52).</summary>
public sealed class ScriptRunView(string scriptName)
{
    public string ScriptName { get; } = scriptName;

    public Queue<ScriptOutputLine> Lines { get; } = new();

    public string StatusText { get; set; } = "";

    public bool StatusIsError { get; set; }
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

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
    private Func<string?> _defaultHostProvider = null!;
    private readonly ObservableCollection<ScriptListItem> _items = new();
    /// <summary>Last run output and status per script name, so switching
    /// scripts and coming back shows that script's own last run. Also the
    /// sink of a run in progress: its output keeps accumulating here while
    /// another script is displayed.</summary>
    private readonly Dictionary<string, ScriptRunView> _runViews = new();
    private CancellationTokenSource? _runSource;
    /// <summary>Unsaved edits (name and text) of scripts that are not the
    /// displayed one, by saved script name, so switching scripts does not
    /// lose them.</summary>
    private readonly Dictionary<string, ScriptDraft> _drafts = new();
    private bool _isLoaded;
    private bool _isRunning;
    private bool _isReloading;
    private bool _lastHasUnsavedChanges;
    private bool _suppressTextChanged;

    /// <summary>Raised when any script gains or loses unsaved changes, so
    /// the host can flag the tab.</summary>
    public event EventHandler<bool>? HasUnsavedChangesChanged;

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
    /// <param name="defaultHostProvider">First host of the configured SSH
    /// servers, or null when there is none — used to prefill a new script.</param>
    public void Initialize(
        ILogger logger, Func<string> scriptsPathProvider, Func<ScriptRunner> runnerFactory, Func<string?> defaultHostProvider)
    {
        _defaultHostProvider = defaultHostProvider;
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
            var selectedItem = ScriptList.SelectedItem as ScriptListItem;
            SetScriptEditor(selectedItem?.Script.Name ?? "", selectedItem?.Script.Body ?? "");
        }

        LoadScripts();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: Scripts Add clicked");
        var path = _scriptsPathProvider();
        var name = NewScriptName();
        var host = _defaultHostProvider() is { Length: > 0 } firstHost ? firstHost : "TODO";

        // The reload below would otherwise drop the unsaved edit of the displayed script.
        CaptureDraft(ScriptList.SelectedItem as ScriptListItem);

        try
        {
            ScriptFileEditor.AddScript(path, name, $"# @host: {host}\n# @description: TODO");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to add script '{Script}' to '{Path}'", name, path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not add a script to the scripts file:\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _logger.LogInformation("Script '{Script}' added to '{Path}'", name, path);
        LoadScripts(name);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_isRunning || ScriptList.SelectedItem is not ScriptListItem item)
            return;

        var name = item.Script.Name;
        var path = _scriptsPathProvider();
        _logger.LogInformation("User action: Scripts Delete clicked for '{Script}'", name);
        if (!GenericMessageDialog.ShowConfirm(
                Window.GetWindow(this), _logger, "Delete script",
                $"Delete the script '{name}'?\n\nIt will be removed from {path}. This cannot be undone.", "Delete", "Cancel"))
            return;

        try
        {
            ScriptFileEditor.RemoveScript(path, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to delete script '{Script}' from '{Path}'", name, path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not delete the script '{name}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _logger.LogInformation("Script '{Script}' deleted from '{Path}'", name, path);
        _drafts.Remove(name);
        _runViews.Remove(name);
        LoadScripts();
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveSelectedScript(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveSelectedScript(1);

    /// <summary>Moves the selected script up (-1) or down (+1) in the file
    /// and keeps it selected.</summary>
    private void MoveSelectedScript(int offset)
    {
        if (ScriptList.SelectedItem is not ScriptListItem item)
            return;

        var name = item.Script.Name;
        var path = _scriptsPathProvider();
        _logger.LogInformation("User action: Scripts Move {Direction} clicked for '{Script}'", offset < 0 ? "up" : "down", name);

        // The reload below would otherwise drop the unsaved edit of the displayed script.
        CaptureDraft(item);

        try
        {
            ScriptFileEditor.MoveScript(path, name, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to move script '{Script}' in '{Path}'", name, path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not move the script '{name}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadScripts(name);
    }

    /// <summary>"new-script", or "new-script-2", "new-script-3"... when
    /// taken: script names must be unique within the file (FR-48.1).</summary>
    private string NewScriptName()
    {
        const string baseName = "new-script";
        var taken = _items.Select(item => item.Script.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(baseName))
            return baseName;

        var suffix = 2;
        while (taken.Contains($"{baseName}-{suffix}"))
            suffix++;
        return $"{baseName}-{suffix}";
    }

    /// <summary>The script Run would start: the selected one as saved, or —
    /// when its text was edited — the edited text parsed the same way as the
    /// file (so a changed `@host` or `@confirm` applies to the run). Null
    /// when nothing is selected or the script is invalid.</summary>
    private ScriptDefinition? GetRunnableScript()
    {
        if (ScriptList.SelectedItem is not ScriptListItem item)
            return null;

        var script = IsDirty() ? ParseEditedText(item.Script.Name) : item.Script;
        return script is { IsValid: true } ? script : null;
    }

    private ScriptDefinition? ParseEditedText(string name)
    {
        var lines = new List<string> { $"[{name}]" };
        lines.AddRange(ScriptText.Text.Replace("\r\n", "\n").Split('\n'));
        var parsed = ScriptFileParser.ParseLines(lines);
        // More than one section: the edit contains a line that looks like a section header.
        return parsed.Count == 1 ? parsed[0] : null;
    }

    private bool HasUnsavedChanges() => _drafts.Count > 0 || IsDirty();

    /// <summary>True when the displayed name or text differs from the
    /// selected script as saved (line endings and surrounding blank lines of
    /// the text ignored, as the file format ignores them).</summary>
    private bool IsDirty() => IsNameDirty() || IsTextDirty();

    private bool IsTextDirty() =>
        ScriptList.SelectedItem is ScriptListItem item && NormalizeBody(ScriptText.Text) != NormalizeBody(item.Script.Body);

    private bool IsNameDirty() =>
        ScriptList.SelectedItem is ScriptListItem item && ScriptNameBox.Text.Trim() != item.Script.Name;

    private static string NormalizeBody(string text) => text.Replace("\r\n", "\n").Trim('\n');

    /// <summary>Keeps the edited name and text of <paramref name="item"/> as
    /// a draft (or drops its draft when nothing differs from the file), so
    /// a reload or a switch of selection does not lose them.</summary>
    private void CaptureDraft(ScriptListItem? item)
    {
        if (item is null)
            return;

        if (NormalizeBody(ScriptText.Text) != NormalizeBody(item.Script.Body) || ScriptNameBox.Text.Trim() != item.Script.Name)
            _drafts[item.Script.Name] = new ScriptDraft(ScriptNameBox.Text, ScriptText.Text);
        else
            _drafts.Remove(item.Script.Name);
    }

    /// <summary>Why the edited name cannot be saved, or null when it can.</summary>
    private string? GetNameError()
    {
        if (ScriptList.SelectedItem is not ScriptListItem item || !IsNameDirty())
            return null;

        var newName = ScriptNameBox.Text.Trim();
        if (newName.Length == 0)
            return "The name must not be empty.";
        if (!ScriptFileParser.IsValidName(newName))
            return "Invalid name: use letters, digits, '_', '.', '-' and spaces (not starting with a space).";
        if (_items.Any(other => other != item && other.Script.Name == newName))
            return $"A script named '{newName}' already exists.";
        // A run's output is filed under the script's name: renaming mid-run would orphan it.
        if (_isRunning)
            return "A script is running: it cannot be renamed until it ends.";

        return null;
    }

    private void SetScriptEditor(string name, string text)
    {
        _suppressTextChanged = true;
        try
        {
            ScriptNameBox.Text = name;
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

    private void OnScriptNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressTextChanged)
            UpdateButtons();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ScriptList.SelectedItem is not ScriptListItem item || !IsDirty() || GetNameError() is not null)
            return;

        var name = item.Script.Name;
        var newName = ScriptNameBox.Text.Trim();
        var path = _scriptsPathProvider();
        _logger.LogInformation("User action: Scripts Save clicked for '{Script}' (new name '{NewName}')", name, newName);
        try
        {
            ScriptFileEditor.ReplaceBody(path, name, ScriptText.Text, newName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to save script '{Script}' to '{Path}'", name, path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not save the script '{name}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _logger.LogInformation("Script '{Script}' saved to '{Path}' as '{NewName}'", name, path, newName);
        _drafts.Remove(name);
        // Carry the last run of a renamed script over to its new name.
        if (newName != name && _runViews.Remove(name, out var renamedRunView))
        {
            renamedRunView.ScriptName = newName;
            _runViews[newName] = renamedRunView;
        }

        LoadScripts(newName);
    }

    /// <summary>Rebuilds the list from the file (FR-49), keeping the
    /// selection when a script of the same name still exists.</summary>
    private void LoadScripts(string? selectName = null)
    {
        var path = _scriptsPathProvider();
        // The path can change with a config reload, so refresh the link on every load.
        OpenFileLink.Inlines.Clear();
        OpenFileLink.Inlines.Add(path);
        var previousName = selectName ?? (ScriptList.SelectedItem as ScriptListItem)?.Script.Name;

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
        SetScriptEditor("", "");
        UpdateButtons();
    }

    private void OnScriptListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The editor still holds the previous script's name and text here: keep them as a draft if edited.
        if (!_isReloading && e.RemovedItems.Count > 0 && e.RemovedItems[0] is ScriptListItem previous)
            CaptureDraft(previous);

        var selected = ScriptList.SelectedItem as ScriptListItem;
        var selectedName = selected?.Script.Name;
        if (selectedName is not null && _drafts.TryGetValue(selectedName, out var draft))
            SetScriptEditor(draft.Name, draft.Text);
        else
            SetScriptEditor(selectedName ?? "", selected?.Script.Body ?? "");
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
        var nameError = GetNameError();
        SaveButton.IsEnabled = IsDirty() && nameError is null;
        NameErrorText.Text = nameError ?? "";
        NameErrorText.Visibility = nameError is null ? Visibility.Collapsed : Visibility.Visible;
        ScriptNameBox.IsReadOnly = selected is null || _isRunning;
        DeleteButton.IsEnabled = !_isRunning && selected is not null;
        MoveUpButton.IsEnabled = selected is not null && ScriptList.SelectedIndex > 0;
        MoveDownButton.IsEnabled = selected is not null && ScriptList.SelectedIndex < _items.Count - 1;
        RunButton.IsEnabled = !_isRunning && GetRunnableScript() is not null;

        var hasUnsavedChanges = HasUnsavedChanges();
        if (hasUnsavedChanges != _lastHasUnsavedChanges)
        {
            _lastHasUnsavedChanges = hasUnsavedChanges;
            HasUnsavedChangesChanged?.Invoke(this, hasUnsavedChanges);
        }
        CancelButton.IsEnabled = _isRunning;
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_isRunning || GetRunnableScript() is not { } script)
            return;

        _logger.LogInformation("User action: Scripts Run clicked for '{Script}' (unsaved edits: {IsDirty})", script.Name, IsDirty());
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

/// <summary>Unsaved edit of one script: its name box and its text.</summary>
public sealed record ScriptDraft(string Name, string Text);

/// <summary>Output and status of the last run of one script (FR-52).</summary>
public sealed class ScriptRunView(string scriptName)
{
    public string ScriptName { get; set; } = scriptName;

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

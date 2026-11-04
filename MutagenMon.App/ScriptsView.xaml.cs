using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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
    /// <summary>Every script of the file, in file order (the tree shows them
    /// grouped).</summary>
    private readonly List<ScriptListItem> _items = new();
    /// <summary>Top-level tree nodes: the groups in order of first appearance
    /// in the file, then the scripts that have no group.</summary>
    private readonly ObservableCollection<ScriptTreeNode> _roots = new();
    /// <summary>Groups the user has expanded; groups start collapsed, and the
    /// state survives a reload (the nodes are rebuilt).</summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
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
    private bool _isCancelling;
    /// <summary>Cached view of the run in progress, so Cancel can update its status line.</summary>
    private ScriptRunView? _runningView;
    private bool _isReloading;
    private bool _lastHasUnsavedChanges;
    private bool _suppressTextChanged;

    /// <summary>Raised when any script gains or loses unsaved changes, so
    /// the host can flag the tab.</summary>
    public event EventHandler<bool>? HasUnsavedChangesChanged;

    public ScriptsView()
    {
        InitializeComponent();
        ScriptTree.ItemsSource = _roots;
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
            var selectedItem = SelectedScript;
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
        CaptureDraft(SelectedScript);

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
        if (_isRunning || SelectedScript is not { } item)
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

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveSelectedNode(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveSelectedNode(1);

    /// <summary>Moves the selected node up (-1) or down (+1) in the file and
    /// keeps it selected: a script moves within its group, a group moves
    /// past its neighbouring group.</summary>
    private void MoveSelectedNode(int offset)
    {
        if (SelectedGroup is { } group)
        {
            MoveGroup(group.Name, offset);
            return;
        }

        if (SelectedScript is not { } item)
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

    private void MoveGroup(string groupName, int offset)
    {
        var path = _scriptsPathProvider();
        _logger.LogInformation("User action: Scripts Move {Direction} clicked for group '{Group}'", offset < 0 ? "up" : "down", groupName);

        try
        {
            ScriptFileEditor.MoveGroup(path, groupName, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to move group '{Group}' in '{Path}'", groupName, path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not move the group '{groupName}':\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Only a group is selected, so the editor holds no edit to capture before the rebuild.
        LoadScripts(selectGroup: groupName);
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
        if (SelectedScript is not { } item)
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
        SelectedScript is { } item && NormalizeBody(ScriptText.Text) != NormalizeBody(item.Script.Body);

    private bool IsNameDirty() =>
        SelectedScript is { } item && ScriptNameBox.Text.Trim() != item.Script.Name;

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
        if (SelectedScript is not { } item || !IsNameDirty())
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

    /// <summary>Saves every script with unsaved changes — the displayed one
    /// and all the drafts — in a single write.</summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (GetNameError() is not null)
            return;

        // The displayed script's edit joins the drafts of the other scripts.
        CaptureDraft(SelectedScript);
        if (_drafts.Count == 0)
            return;

        var edits = _drafts.Select(draft_ => new ScriptEdit(draft_.Key, draft_.Value.Text, draft_.Value.Name.Trim())).ToList();
        var path = _scriptsPathProvider();
        _logger.LogInformation("User action: Scripts Save clicked for {Count} script(s)", edits.Count);
        // A run's output is filed under the script's name: renaming mid-run would orphan it.
        if (_isRunning && edits.Any(edit_ => edit_.NewName != edit_.ScriptName))
        {
            MessageBox.Show(
                Window.GetWindow(this), "A script is running: scripts cannot be renamed until it ends.",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ScriptFileEditor.ReplaceBodies(path, edits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to save {Count} script(s) to '{Path}'", edits.Count, path);
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not save the scripts (nothing was written):\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _logger.LogInformation("{Count} script(s) saved to '{Path}'", edits.Count, path);
        var selectedName = SelectedScript?.Script.Name;
        string? newSelectedName = null;
        foreach (var edit in edits)
        {
            if (edit.ScriptName == selectedName)
                newSelectedName = edit.NewName;
            // Carry the last run of a renamed script over to its new name.
            if (edit.NewName != edit.ScriptName && _runViews.Remove(edit.ScriptName, out var renamedRunView))
            {
                renamedRunView.ScriptName = edit.NewName!;
                _runViews[edit.NewName!] = renamedRunView;
            }
        }

        _drafts.Clear();
        LoadScripts(newSelectedName ?? selectedName);
    }

    /// <summary>Rebuilds the list from the file (FR-49), keeping the
    /// selection when a script of the same name still exists.</summary>
    private void LoadScripts(string? selectName = null, string? selectGroup = null)
    {
        var path = _scriptsPathProvider();
        // The path can change with a config reload, so refresh the link on every load.
        OpenFileLink.Inlines.Clear();
        OpenFileLink.Inlines.Add(path);
        var previousName = selectName ?? SelectedScript?.Script.Name;
        var previousGroup = selectGroup ?? (selectName is null ? SelectedGroup?.Name : null);

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
                _roots.Clear();
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
            _roots.Clear();
            foreach (var script in scripts)
                _items.Add(new ScriptListItem(script));

            BuildTree();
            SelectNode(previousName, previousGroup);
            // The tree selects through item containers, which are generated on layout: force it here so the selection (and its changed event) lands inside this reload.
            ScriptTree.UpdateLayout();
        }
        finally
        {
            _isReloading = false;
        }

        UpdateButtons();
    }

    /// <summary>Fills <see cref="_roots"/> from <see cref="_items"/>: one
    /// collapsed node per group (order of first appearance), then the
    /// scripts that have no group.</summary>
    private void BuildTree()
    {
        var groupNames = _items
            .Select(item_ => item_.Script.Group)
            .Where(group_ => group_.Length > 0)
            .Distinct(StringComparer.Ordinal);
        foreach (var groupName in groupNames)
        {
            var node = new ScriptGroupNode(groupName) { IsExpanded = _expandedGroups.Contains(groupName) };
            node.PropertyChanged += OnGroupNodePropertyChanged;
            foreach (var item in _items.Where(item_ => item_.Script.Group == groupName))
                node.Children.Add(item);
            _roots.Add(node);
        }

        foreach (var item in _items.Where(item_ => item_.Script.Group.Length == 0))
            _roots.Add(item);
    }

    /// <summary>Selects the script <paramref name="scriptName"/> (expanding
    /// its group) or, failing that, the group <paramref name="groupName"/>.</summary>
    private void SelectNode(string? scriptName, string? groupName)
    {
        var script = _items.FirstOrDefault(item_ => item_.Script.Name == scriptName);
        if (script is not null)
        {
            _roots.OfType<ScriptGroupNode>().FirstOrDefault(node_ => node_.Name == script.Script.Group)?.Expand();
            script.IsSelected = true;
            return;
        }

        var group = _roots.OfType<ScriptGroupNode>().FirstOrDefault(node_ => node_.Name == groupName);
        if (group is not null)
            group.IsSelected = true;
    }

    private void OnGroupNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScriptTreeNode.IsExpanded) || sender is not ScriptGroupNode node)
            return;

        if (node.IsExpanded)
            _expandedGroups.Add(node.Name);
        else
            _expandedGroups.Remove(node.Name);
    }

    /// <summary>A click on a group's name opens or closes it (the expander
    /// arrow keeps its own behaviour).</summary>
    private void OnScriptTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<ToggleButton>(source) is not null)
            return;
        if (FindAncestor<TreeViewItem>(source) is not { DataContext: ScriptGroupNode node })
            return;

        node.IsSelected = true;
        node.IsExpanded = !node.IsExpanded;
        // Handled, so the default double-click expansion does not toggle it a second time.
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return null;
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

    private void OnScriptTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // The editor still holds the previous script's name and text here: keep them as a draft if edited.
        if (!_isReloading && e.OldValue is ScriptListItem previous)
            CaptureDraft(previous);

        var selected = SelectedScript;
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

    private ScriptListItem? SelectedScript => ScriptTree.SelectedItem as ScriptListItem;

    private ScriptGroupNode? SelectedGroup => ScriptTree.SelectedItem as ScriptGroupNode;

    private bool IsSelected(string scriptName) =>
        SelectedScript?.Script.Name == scriptName;

    private void UpdateButtons()
    {
        var selected = SelectedScript;
        var nameError = GetNameError();
        SaveButton.IsEnabled = HasUnsavedChanges() && nameError is null;
        NameErrorText.Text = nameError ?? "";
        NameErrorText.Visibility = nameError is null ? Visibility.Collapsed : Visibility.Visible;
        ScriptNameBox.IsReadOnly = selected is null || _isRunning;
        DeleteButton.IsEnabled = !_isRunning && selected is not null;
        MoveUpButton.IsEnabled = CanMoveSelectedNode(-1);
        MoveDownButton.IsEnabled = CanMoveSelectedNode(1);
        RunButton.IsEnabled = !_isRunning && GetRunnableScript() is not null;

        var hasUnsavedChanges = HasUnsavedChanges();
        if (hasUnsavedChanges != _lastHasUnsavedChanges)
        {
            _lastHasUnsavedChanges = hasUnsavedChanges;
            HasUnsavedChangesChanged?.Invoke(this, hasUnsavedChanges);
        }
        CancelButton.IsEnabled = _isRunning && !_isCancelling;
    }

    /// <summary>True when the selected script has a neighbour to swap with
    /// in its group, or the selected group a neighbouring group.</summary>
    private bool CanMoveSelectedNode(int offset)
    {
        List<string> siblings;
        string selectedKey;
        if (SelectedGroup is { } group)
        {
            siblings = _roots.OfType<ScriptGroupNode>().Select(node_ => node_.Name).ToList();
            selectedKey = group.Name;
        }
        else if (SelectedScript is { } item)
        {
            siblings = _items.Where(item_ => item_.Script.Group == item.Script.Group).Select(item_ => item_.Script.Name).ToList();
            selectedKey = item.Script.Name;
        }
        else
            return false;

        var target = siblings.IndexOf(selectedKey) + offset;
        return target >= 0 && target < siblings.Count;
    }

    /// <summary>Same as clicking Run (F5 shortcut): does nothing when Run
    /// is disabled.</summary>
    public void RunSelectedScript()
    {
        if (RunButton.IsEnabled)
            OnRunClick(RunButton, new RoutedEventArgs());
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
        _runningView = runView;
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
            _runningView = null;
            _isRunning = false;
            _isCancelling = false;
            UpdateButtons();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: Scripts Cancel clicked");
        if (_runSource is null || _runningView is null || _isCancelling)
            return;

        _isCancelling = true;
        // The run only reports "Cancelled" once ssh is killed and has exited: say so right away.
        SetStatus(_runningView, "Cancelling...", isError: false);
        CancelButton.IsEnabled = false;
        _runSource.Cancel();
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

/// <summary>Node of the scripts tree; selection and expansion are bound
/// two-way to the tree's item containers.</summary>
public abstract class ScriptTreeNode : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public void Expand() => IsExpanded = true;

    private void SetField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Tree node for one script group (`@group`, FR-48.5): its
/// scripts are the children.</summary>
public sealed class ScriptGroupNode(string name) : ScriptTreeNode
{
    public string Name { get; } = name;

    public ObservableCollection<ScriptListItem> Children { get; } = new();

    public string Header => $"{Name} ({Children.Count})";
}

/// <summary>Tree row for one script: the name, and either the description
/// or, for an invalid script, the reason it cannot run (FR-49.2).</summary>
public sealed class ScriptListItem(ScriptDefinition script) : ScriptTreeNode
{
    public ScriptDefinition Script { get; } = script;

    public string Name => Script.Name;

    public bool IsInvalid => !Script.IsValid;

    public string Detail => Script.IsValid ? Script.Description : $"Invalid: {Script.ValidationError}";
}

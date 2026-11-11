using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Scripts;
using MutagenMon.Core.Status;

namespace MutagenMon.App;

/// <summary>
/// Implements the full session status view (FR-8), with a conflicts section and "Resolve
/// conflicts" action when at least one conflict is not auto-resolved
/// (FR-8.1/FR-8.2), or a plain informational OK dialog otherwise (FR-8.3).
/// Closing hides rather than closes the window, so re-opening it doesn't
/// reconstruct it from scratch — matches NFR-7's "no main window, dialogs
/// on demand" model.
/// </summary>
public partial class StatusWindow : Window
{
    /// <summary>Raised when the user clicks "Resolve conflicts" (FR-8.2) —
    /// the actual FR-9 workflow is composed and run by the caller
    /// (App.xaml.cs), which owns the DI-provided services it needs.</summary>
    public event EventHandler? ResolveConflictsRequested;

    /// <summary>Raised when the user clicks "Reload config" — handled by
    /// App.xaml.cs, which owns the monitor service and tray icon controller
    /// (FR-7.1).</summary>
    public event EventHandler? ReloadConfigRequested;

    /// <summary>Raised when the user clicks "Stop/Start Mutagen sessions" —
    /// handled by App.xaml.cs, which owns the monitor service (FR-7.2).</summary>
    public event EventHandler? ToggleMonitoringRequested;

    /// <summary>Raised when the user clicks "Exit" — handled by App.xaml.cs,
    /// which owns the host lifetime.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised once the user has stopped resizing the window, with
    /// its new size — handled by App.xaml.cs, which saves it to the
    /// configuration file.</summary>
    public event EventHandler<Size>? SizeSettled;

    /// <summary>Raised when the user clicks the toolbar's "Add" action
    /// (FR-16.1) — handled by App.xaml.cs, which owns
    /// <c>SessionEditingService</c> and the session definitions.</summary>
    public event EventHandler? AddSessionRequested;

    /// <summary>Raised when the user clicks the toolbar's "Duplicate" action,
    /// with the selected session's name — handled by App.xaml.cs, which owns
    /// <c>SessionEditingService</c> and the session definitions.</summary>
    public event EventHandler<string>? DuplicateSessionRequested;

    /// <summary>Raised when the user clicks a row's View sync status icon
    /// (FR-28), with that session's name.</summary>
    public event EventHandler<string>? ViewSyncStatusRequested;

    /// <summary>Raised when the user clicks a row's Edit icon (FR-17.2),
    /// with that session's name.</summary>
    public event EventHandler<string>? EditSessionRequested;

    /// <summary>Raised when the user clicks a row's Delete icon (FR-17.3),
    /// with that session's name.</summary>
    public event EventHandler<string>? DeleteSessionRequested;

    /// <summary>Bound once to <c>SessionsGrid.ItemsSource</c> and updated
    /// in place on every refresh (<see cref="SyncRows"/>) rather than
    /// replaced — while the window stays open it's refreshed roughly once a
    /// second (FR-8.4), and rebinding a fresh list every tick would rebuild
    /// every row (losing selection/scroll position, and flickering) even
    /// when nothing actually changed.</summary>
    private readonly ObservableCollection<SessionSummaryRow> _sessionRows = new();
    /// <summary>Name of the session selected in the grid, kept apart from
    /// <c>SessionsGrid.SelectedItem</c> because the refresh replaces a row whose
    /// data changed (<see cref="SyncRows"/>), which clears the grid's selection;
    /// the `{{session:…}}` macros (FR-55.3) read it through
    /// <see cref="GetSelectedSession"/>. Cleared when the user deselects the
    /// row or the session disappears.</summary>
    private string? _selectedSessionName;
    /// <summary>True while <see cref="SyncRows"/> runs: a selection change
    /// then comes from the refresh, not from the user.</summary>
    private bool _isSyncingRows;
    private readonly ILogger _logger;
    /// <summary>Runs the scripts of every tab, so only one script runs at a time (FR-51.1, FR-56.6).</summary>
    private readonly ScriptRunCoordinator _scriptRunCoordinator;
    private readonly Func<string> _scriptsPathProvider;
    /// <summary>"MutagenMon" and its version: the title when no script runs.</summary>
    private readonly string _baseTitle;
    /// <summary>Waits for a resize to stop before <see cref="SizeSettled"/>:
    /// a drag raises <see cref="FrameworkElement.SizeChanged"/> many times a
    /// second, which must not become as many writes of the config file.</summary>
    private readonly DispatcherTimer _sizeSettledTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public StatusWindow(ILogger logger, IconImageCache iconCache, FileLoggerProvider loggerProvider, bool showGenerateException,
        Func<string> scriptsPathProvider, Func<ScriptRunner> scriptRunnerFactory, Func<string?> defaultScriptHostProvider)
    {
        InitializeComponent();
        _sizeSettledTimer.Tick += (_, _) => RaiseSizeSettled();
        SizeChanged += (_, _) =>
        {
            // A maximized or minimized window's size is not the user's chosen size.
            if (WindowState == WindowState.Normal)
            {
                _sizeSettledTimer.Stop();
                _sizeSettledTimer.Start();
            }
        };
        _baseTitle = $"MutagenMon {App.AppVersion}";
        Title = _baseTitle;
        SessionsGrid.ItemsSource = _sessionRows;
        ((IconKeyToImageSourceConverter)Resources["IconKeyToImageSourceConverter"]).Cache = iconCache;
        _logger = logger;
        MutagenConfigEditorViewControl.Initialize(logger);
        MutagenMonitorConfigEditorViewControl.Initialize(logger);
        LogsViewControl.Initialize(logger, loggerProvider, showGenerateException);
        _scriptsPathProvider = scriptsPathProvider;
        _scriptRunCoordinator = new ScriptRunCoordinator(logger, scriptRunnerFactory, Dispatcher);
        // FR-56.6: a run starting or ending on any tab changes whether Run is offered here.
        _scriptRunCoordinator.RunningChanged += (_, _) => UpdateRunSessionScriptButton();
        // FR-52.6: while a script runs, the title also shows which one and for how long.
        _scriptRunCoordinator.RunInfoChanged += (_, info) => Title = info is null ? _baseTitle : $"{_baseTitle}  -  {info}";
        ScriptsViewControl.Initialize(
            logger, scriptsPathProvider, _scriptRunCoordinator, defaultScriptHostProvider,
            GetSelectedSession);
        ScriptsViewControl.HasUnsavedChangesChanged += (_, hasUnsavedChanges) => UpdateScriptsTabHeader(hasUnsavedChanges);
        ScriptsViewControl.ScriptsChanged += (_, _) => RefreshSessionScripts();
        // FR-56.2: the file can also be edited outside the application: rebuild the combo when the window comes back to the front.
        Activated += (_, _) => RefreshSessionScripts();
    }

    /// <summary>F5 runs the selected script on the Scripts tab (FR-47.2) and the
    /// script chosen in the combo on the selected session on the Sync tab
    /// (FR-56.3); on the other tabs the key keeps its default behaviour.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5)
            return;

        if (ScriptsTab.IsSelected)
        {
            e.Handled = true;
            ScriptsViewControl.RunSelectedScript();
        }
        else if (SyncTab.IsSelected)
        {
            e.Handled = true;
            RunSessionScript();
        }
    }

    /// <summary>Rebuilds the session-script combo (FR-56.2) from the scripts file
    /// as saved on disk: the valid scripts tagged <c>@session-script</c>, in file
    /// order. The selected script stays selected while its name is still listed.
    /// A missing or unreadable file gives an empty, disabled combo and no message
    /// (the Scripts tab reports it, FR-49.2).</summary>
    public void RefreshSessionScripts()
    {
        var previousName = (SessionScriptCombo.SelectedItem as ScriptDefinition)?.Name;
        IReadOnlyList<ScriptDefinition> scripts;
        try
        {
            scripts = ScriptFileParser.ParseFile(_scriptsPathProvider())
                .Where(script_ => script_.IsSessionScript && script_.IsValid)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The session-script combo is empty: the scripts file cannot be read");
            scripts = [];
        }

        SessionScriptCombo.ItemsSource = scripts;
        SessionScriptCombo.SelectedItem = scripts.FirstOrDefault(script_ => script_.Name == previousName);
        SessionScriptCombo.IsEnabled = scripts.Count > 0;
        UpdateRunSessionScriptButton();
    }

    private void OnSessionScriptSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRunSessionScriptButton();

    /// <summary>FR-56.3: Run needs a session selected in the grid (the one the
    /// macros would use, FR-55.3), a script selected in the combo and no script
    /// running on any tab.</summary>
    private void UpdateRunSessionScriptButton() =>
        RunSessionScriptButton.IsEnabled = !_scriptRunCoordinator.IsRunning
            && SessionScriptCombo.SelectedItem is ScriptDefinition
            && GetSelectedSession() is not null;

    /// <summary>The Run button (FR-56.3); see <see cref="RunSessionScript"/>.</summary>
    private void OnRunSessionScriptClick(object sender, RoutedEventArgs e) => RunSessionScript();

    /// <summary>FR-56.4: runs the script chosen in the combo on the selected session. The
    /// file is read again so the run uses the script as saved now; the session is read once,
    /// here. Declining the `@confirm` confirmation opens nothing; a macro error opens the dialog
    /// with the message (FR-56.5).</summary>
    private void RunSessionScript()
    {
        if (!RunSessionScriptButton.IsEnabled)
            return;

        _logger.LogInformation("User action: status window Run session script requested ('{Script}')",
            (SessionScriptCombo.SelectedItem as ScriptDefinition)?.Name);
        RefreshSessionScripts();
        if (SessionScriptCombo.SelectedItem is not ScriptDefinition script || GetSelectedSession() is not { } session)
        {
            _logger.LogInformation("Run session script: the script or the session is no longer available");
            return;
        }

        _logger.LogDebug("Run session script: '{Script}' on session '{Session}'", script.Name, session.Name);
        var preparation = _scriptRunCoordinator.Prepare(this, script, session);
        if (preparation is null)
            return;

        ScriptRunWindow.ShowRun(this, _logger, _scriptRunCoordinator, preparation, session.Name);
    }

    /// <summary>"Scripts", or a bold "Scripts *" while a script has unsaved changes.</summary>
    private void UpdateScriptsTabHeader(bool hasUnsavedChanges)
    {
        if (!hasUnsavedChanges)
        {
            ScriptsTab.Header = "Scripts";
            return;
        }

        ScriptsTab.Header = new TextBlock { Text = "Scripts *", FontWeight = FontWeights.Bold };
    }

    public void UpdateContent(MonitorSnapshot snapshot, IReadOnlyList<string> sessionNames, bool reloadInProgress)
    {
        var rows = BuildSessionRows(
            sessionNames, snapshot.SessionStatuses, snapshot.LastChangedUtc, snapshot.SessionCodes, snapshot.Conflicts, snapshot.Enabled);
        _isSyncingRows = true;
        try
        {
            SyncRows(_sessionRows, rows);
        }
        finally
        {
            _isSyncingRows = false;
        }

        RestoreSessionSelection();
        // The session selected may have disappeared in this refresh.
        UpdateRunSessionScriptButton();

        ConflictsText.Text = BuildConflictsSection(sessionNames, snapshot.Conflicts);

        var hasUnresolvedConflicts = HasUnresolvedConflicts(snapshot.Conflicts);
        ResolveConflictsButton.Visibility = hasUnresolvedConflicts ? Visibility.Visible : Visibility.Collapsed;

        ToggleMonitoringButton.Content = snapshot.Enabled ? "Stop Mutagen sessions" : "Start Mutagen sessions";

        // Mirrors FR-7.5's tray-menu collapse while a reload drains and
        // rebuilds the monitor stack — prevents firing a second reload or
        // toggling monitoring against a service that's about to be replaced.
        ReloadConfigButton.IsEnabled = !reloadInProgress;
        ToggleMonitoringButton.IsEnabled = !reloadInProgress;
        ExitButton.IsEnabled = !reloadInProgress;
    }

    /// <summary>Updates <paramref name="target"/> to match
    /// <paramref name="source"/> element-by-element (index-based, since row
    /// order is always <c>sessionNames</c> order) instead of clearing and
    /// re-adding everything — <see cref="SessionSummaryRow"/> is a record,
    /// so the equality check skips any row whose data is unchanged since the
    /// last refresh, avoiding needless DataGrid re-rendering.</summary>
    private static void SyncRows(ObservableCollection<SessionSummaryRow> target, IReadOnlyList<SessionSummaryRow> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i >= target.Count)
                target.Add(source[i]);
            else if (!target[i].Equals(source[i]))
                target[i] = source[i];
        }

        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }

    private void OnResolveConflictsClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: resolve conflicts clicked");
        ResolveConflictsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReloadConfigClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Reload config clicked");
        ReloadConfigRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddSessionClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Add session clicked");
        AddSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDuplicateSessionClick(object sender, RoutedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionSummaryRow row)
            return;
        _logger.LogInformation("User action: status window Duplicate session clicked ({Name})", row.Name);
        DuplicateSessionRequested?.Invoke(this, row.Name);
    }

    /// <summary>The toolbar's "Duplicate" button (FR-16.1-style, but unlike
    /// Add it needs a source session) only makes sense once a row is
    /// selected.</summary>
    private void OnSessionsGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DuplicateSessionButton.IsEnabled = SessionsGrid.SelectedItem is SessionSummaryRow;
        if (SessionsGrid.SelectedItem is SessionSummaryRow row)
            _selectedSessionName = row.Name;
        // Otherwise the user cleared the selection (the refresh's own clearing is restored in UpdateContent).
        else if (!_isSyncingRows)
            _selectedSessionName = null;
        UpdateRunSessionScriptButton();
    }

    /// <summary>The current row of the selected session, or null when none
    /// is selected or it no longer exists (FR-55.3).</summary>
    private SessionSummaryRow? GetSelectedSession() =>
        _sessionRows.FirstOrDefault(row_ => row_.Name == _selectedSessionName);

    /// <summary>After a refresh: forgets the selected session when it no
    /// longer exists, and keeps its (possibly replaced) row visibly selected,
    /// so the grid always shows the session the macros use.</summary>
    private void RestoreSessionSelection()
    {
        if (_selectedSessionName is null)
            return;

        var row = GetSelectedSession();
        if (row is null)
            _selectedSessionName = null;
        else if (!ReferenceEquals(SessionsGrid.SelectedItem, row))
            SessionsGrid.SelectedItem = row;
    }

    /// <summary>Double-clicking a status row opens the same Edit flow as the
    /// row's Edit icon (FR-17.2). Ignored when the double-click landed on one
    /// of the row's own action buttons (View/Edit/Delete) — those already
    /// handle their own clicks, so this only fires for a double-click
    /// anywhere else on the row.</summary>
    private void OnSessionsGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestorOrSelf<ButtonBase>(source) is not null)
            return;
        if (FindAncestorOrSelf<DataGridRow>(source) is not { Item: SessionSummaryRow row })
            return;

        _logger.LogInformation("User action: status window session row double-clicked ({Name})", row.Name);
        EditSessionRequested?.Invoke(this, row.Name);
    }

    private static T? FindAncestorOrSelf<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
                return match;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void OnViewSyncStatusClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SessionSummaryRow row)
            return;
        _logger.LogInformation("User action: status window View sync status clicked ({Name})", row.Name);
        ViewSyncStatusRequested?.Invoke(this, row.Name);
    }

    private void OnEditSessionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SessionSummaryRow row)
            return;
        _logger.LogInformation("User action: status window Edit session clicked ({Name})", row.Name);
        EditSessionRequested?.Invoke(this, row.Name);
    }

    private void OnDeleteSessionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SessionSummaryRow row)
            return;
        _logger.LogInformation("User action: status window Delete session clicked ({Name})", row.Name);
        DeleteSessionRequested?.Invoke(this, row.Name);
    }

    private void OnToggleMonitoringClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Stop/Start sessions clicked");
        ToggleMonitoringRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the scripts with unsaved changes (FR-47.5), for the
    /// application exit. Returns false when the save was refused or failed:
    /// the caller must not exit. The window is brought to the front on the
    /// Scripts tab while there is something to save (the message boxes of the
    /// save need a visible owner, and a failure must show the pending edit).
    /// When the save succeeds the caller hides it again at once.</summary>
    public bool TrySaveScripts()
    {
        if (!ScriptsViewControl.HasUnsavedChanges())
            return true;

        ScriptsTab.IsSelected = true;
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        return ScriptsViewControl.TrySaveAll();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: status window Exit clicked");
        // Not hidden here: the caller (App.ExitAsync) asks for confirmation
        // first and only hides/closes everything once the user confirms.
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        // A resize just before closing must not be lost to the timer.
        if (_sizeSettledTimer.IsEnabled)
            RaiseSizeSettled();
        Hide();
    }

    /// <summary>Sets the window size restored from the configuration,
    /// clamped between the window's minimum size and the screen's work area
    /// so a stale or hand-edited value cannot make it unusable. The position
    /// is left to Windows.</summary>
    public void RestoreSize(int width, int height)
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, workArea.Width));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, workArea.Height));
    }

    private void RaiseSizeSettled()
    {
        _sizeSettledTimer.Stop();
        if (WindowState == WindowState.Normal)
            SizeSettled?.Invoke(this, new Size(ActualWidth, ActualHeight));
    }

    /// <summary>One row per session (FR-8.1), in <paramref name="sessionNames"/>
    /// order, for the status view's grid.</summary>
    private static IReadOnlyList<SessionSummaryRow> BuildSessionRows(
        IReadOnlyCollection<string> sessionNames,
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses,
        IReadOnlyDictionary<string, DateTimeOffset?> lastChangedUtc,
        IReadOnlyDictionary<string, SessionStatusCode> sessionCodes,
        IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts,
        bool enabled)
    {
        var rows = new List<SessionSummaryRow>();
        foreach (var name in sessionNames)
        {
            statuses.TryGetValue(name, out var status);
            lastChangedUtc.TryGetValue(name, out var lastChanged);
            sessionCodes.TryGetValue(name, out var code);

            var conflictCount = conflicts.TryGetValue(name, out var sessionConflicts)
                ? sessionConflicts.Count(c => !c.AutoResolved)
                : 0;

            rows.Add(new SessionSummaryRow(
                name,
                TrayIconStateResolver.ResolveSessionIconKey(code, enabled),
                BuildStatusDisplay(status),
                conflictCount,
                status?.Alpha?.Url ?? "(unknown)",
                status?.Beta?.Url ?? "(unknown)",
                lastChanged));
        }

        return rows;
    }

    /// <summary>While a large file is actively transferring, the bare
    /// "Staging files on &lt;side&gt;" status text never changes and gives no
    /// indication of progress (FR-2.2). When <see cref="StagingProgress"/> is
    /// available, show which file is currently uploading and how much data
    /// has moved instead. <see cref="StagingProgress.FilesCompleted"/> counts
    /// fully-finished files, so it is bumped by one here to report the file
    /// *in progress* (e.g. "0/2" while the first of two files is uploading
    /// becomes "1/2", not "0/2").</summary>
    private static string BuildStatusDisplay(ParsedSessionStatus? status)
    {
        if (status is null || string.IsNullOrEmpty(status.Status))
            return "(not running)";

        if (status.Staging is { CurrentFileName: { } fileName } staging)
        {
            var filesInProgress = Math.Min(staging.FilesCompleted + 1, staging.FilesTotal);
            return $"Uploading {filesInProgress}/{staging.FilesTotal} , {fileName} , {staging.BytesTransferred}";
        }

        return status.Status;
    }

    /// <summary>Lists every conflict (autoresolving ones annotated) —
    /// "(none)" if there are none at all. The "Conflicts:" label itself
    /// lives in the XAML, next to <c>ConflictsText</c>.</summary>
    private static string BuildConflictsSection(
        IReadOnlyCollection<string> sessionNames, IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts)
    {
        var sb = new StringBuilder();
        foreach (var name in sessionNames)
        {
            if (!conflicts.TryGetValue(name, out var list))
                continue;
            foreach (var conflict in list)
            {
                sb.Append(name).Append(": ").Append(conflict.AlphaName);
                if (conflict.AutoResolved)
                    sb.Append(" [autoresolving]");
                sb.Append('\n');
            }
        }

        if (sb.Length == 0)
            return "(none)";

        return sb.ToString();
    }

    /// <summary>True if at least one conflict is
    /// NOT auto-resolved — this, not the mere presence of a conflict, decides
    /// whether the status view offers "Resolve conflicts" (FR-8.2) or is
    /// purely informational (FR-8.3).</summary>
    private static bool HasUnresolvedConflicts(IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflicts) =>
        conflicts.Values.Any(list => list.Any(c => !c.AutoResolved));
}

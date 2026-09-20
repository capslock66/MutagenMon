using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Sessions;

namespace MutagenMon.App;

/// <summary>
/// Add/Edit session window (FR-18 through FR-27,
/// requirements/07-session-management-requirements.md). Reads/writes
/// every field directly from/to plain WPF controls at load/Save time —
/// no continuous data-binding to <see cref="SessionCommandLine"/>, matching
/// this app's existing code-behind style (no MVVM/INotifyPropertyChanged
/// anywhere else in it). Only produces a validated <see
/// cref="Result"/> on Save; it never itself talks to the `mutagen` CLI or
/// `mutagen-create.bat` — that's <see cref="SessionEditingService"/>,
/// invoked by the caller (the status view, FR-16/FR-17).
/// </summary>
public partial class SessionEditWindow : Window
{
    private readonly ILogger _logger;
    private readonly HashSet<string> _otherSessionNames;
    private readonly IReadOnlyList<SshServerEntry> _sshServers;
    private readonly List<UnknownFlagItem> _unknownFlags = new();

    /// <summary>The session's name before this edit, for the caller to
    /// know which line to replace (FR-27.4.3) — same as <see
    /// cref="Result"/>'s <c>Name</c> unless the user renamed it. Null in
    /// Add mode.</summary>
    public string? OriginalName { get; }

    /// <summary>Set only when the window closes via Save (FR-18.4's
    /// validation already passed at that point).</summary>
    public SessionCommandLine? Result { get; private set; }

    /// <summary>
    /// </summary>
    /// <param name="existing">Null for Add mode. In Edit mode, the
    /// session's current definition to pre-populate every field from
    /// (FR-17.2).</param>
    /// <param name="allSessionNames">Every currently-configured session's
    /// name, used for the FR-18.2 uniqueness check — the session being
    /// edited is excluded from that check by name, not by reference, so a
    /// caller can pass the same full list regardless of mode.</param>
    /// <param name="sshServers">FR-18.5's "Browse SSH server…" picklist,
    /// sourced from <see cref="MutagenMonOptions.SshServers"/>.</param>
    public SessionEditWindow(
        SessionCommandLine? existing, IReadOnlyCollection<string> allSessionNames,
        IReadOnlyList<SshServerEntry> sshServers, ILogger logger)
    {
        InitializeComponent();
        _logger = logger;
        _sshServers = sshServers;

        OriginalName = existing?.Name;
        _otherSessionNames = new HashSet<string>(allSessionNames, StringComparer.Ordinal);
        if (OriginalName is not null)
            _otherSessionNames.Remove(OriginalName);

        Title = existing is null ? "MutagenMon: Add session" : $"MutagenMon: Edit session {existing.Name}";

        PopulateCombos();
        LoadFrom(existing ?? new SessionCommandLine());
        UpdateSaveEnabled();
    }

    private void PopulateCombos()
    {
        SetEnumItems<SyncMode>(SyncModeCombo, includeBlank: false);
        SetEnumItems<PermissionsMode>(PermissionsModeCombo, includeBlank: false);
        SetEnumItems<SymlinkMode>(SymlinkModeCombo, includeBlank: false);

        SetEnumItems<WatchMode>(WatchModeSessionCombo, includeBlank: false);
        SetEnumItems<WatchMode>(WatchModeAlphaCombo, includeBlank: true);
        SetEnumItems<WatchMode>(WatchModeBetaCombo, includeBlank: true);

        SetEnumItems<ProbeMode>(ProbeModeSessionCombo, includeBlank: false);
        SetEnumItems<ProbeMode>(ProbeModeAlphaCombo, includeBlank: true);
        SetEnumItems<ProbeMode>(ProbeModeBetaCombo, includeBlank: true);

        SetEnumItems<ScanMode>(ScanModeSessionCombo, includeBlank: false);
        SetEnumItems<ScanMode>(ScanModeAlphaCombo, includeBlank: true);
        SetEnumItems<ScanMode>(ScanModeBetaCombo, includeBlank: true);

        SetEnumItems<StageMode>(StageModeSessionCombo, includeBlank: false);
        SetEnumItems<StageMode>(StageModeAlphaCombo, includeBlank: true);
        SetEnumItems<StageMode>(StageModeBetaCombo, includeBlank: true);
    }

    private void LoadFrom(SessionCommandLine model)
    {
        NameBox.Text = model.Name;
        AlphaBox.Text = model.Alpha;
        BetaBox.Text = model.Beta;

        SelectValue(SyncModeCombo, model.Mode);
        IgnoreBox.Text = JoinLines(model.Ignores);
        IgnoreVcsCheck.IsChecked = model.IgnoreVcs;
        IgnoreSyntaxDockerCheck.IsChecked = model.IgnoreSyntaxDocker;

        SelectValue(PermissionsModeCombo, model.PermissionsMode);
        OwnerSessionBox.Text = model.Owner.Session;
        OwnerAlphaBox.Text = model.Owner.Alpha;
        OwnerBetaBox.Text = model.Owner.Beta;
        GroupSessionBox.Text = model.Group.Session;
        GroupAlphaBox.Text = model.Group.Alpha;
        GroupBetaBox.Text = model.Group.Beta;
        FileModeSessionBox.Text = model.FileMode.Session;
        FileModeAlphaBox.Text = model.FileMode.Alpha;
        FileModeBetaBox.Text = model.FileMode.Beta;
        DirectoryModeSessionBox.Text = model.DirectoryMode.Session;
        DirectoryModeAlphaBox.Text = model.DirectoryMode.Alpha;
        DirectoryModeBetaBox.Text = model.DirectoryMode.Beta;

        SelectValue(SymlinkModeCombo, model.SymlinkMode);

        SelectValue(WatchModeSessionCombo, model.WatchMode.Session ?? WatchMode.Portable);
        SelectValue(WatchModeAlphaCombo, model.WatchMode.Alpha);
        SelectValue(WatchModeBetaCombo, model.WatchMode.Beta);
        WatchPollingIntervalSessionBox.Text = model.WatchPollingInterval.Session?.ToString() ?? "";
        WatchPollingIntervalAlphaBox.Text = model.WatchPollingInterval.Alpha?.ToString() ?? "";
        WatchPollingIntervalBetaBox.Text = model.WatchPollingInterval.Beta?.ToString() ?? "";

        SelectValue(ProbeModeSessionCombo, model.ProbeMode.Session ?? ProbeMode.Probe);
        SelectValue(ProbeModeAlphaCombo, model.ProbeMode.Alpha);
        SelectValue(ProbeModeBetaCombo, model.ProbeMode.Beta);

        SelectValue(ScanModeSessionCombo, model.ScanMode.Session ?? ScanMode.Accelerated);
        SelectValue(ScanModeAlphaCombo, model.ScanMode.Alpha);
        SelectValue(ScanModeBetaCombo, model.ScanMode.Beta);

        SelectValue(StageModeSessionCombo, model.StageMode.Session ?? StageMode.Mutagen);
        SelectValue(StageModeAlphaCombo, model.StageMode.Alpha);
        SelectValue(StageModeBetaCombo, model.StageMode.Beta);

        MaxStagingFileSizeBox.Text = model.MaxStagingFileSize ?? "";
        MaxEntryCountBox.Text = model.MaxEntryCount?.ToString() ?? "";

        _unknownFlags.Clear();
        _unknownFlags.AddRange(model.UnknownFlags.Select(f => new UnknownFlagItem { Text = f.Text, Keep = f.Keep }));
        UnknownFlagsList.ItemsSource = _unknownFlags;
        UnknownFlagsTab.Visibility = _unknownFlags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SessionCommandLine BuildResult() => new()
    {
        Name = NameBox.Text.Trim(),
        Alpha = AlphaBox.Text.Trim(),
        Beta = BetaBox.Text.Trim(),
        Mode = GetValue<SyncMode>(SyncModeCombo),
        Ignores = SplitLines(IgnoreBox.Text),
        IgnoreVcs = IgnoreVcsCheck.IsChecked,
        IgnoreSyntaxDocker = IgnoreSyntaxDockerCheck.IsChecked == true,
        PermissionsMode = GetValue<PermissionsMode>(PermissionsModeCombo),
        Owner = new PerSideText(TextOrNull(OwnerSessionBox), TextOrNull(OwnerAlphaBox), TextOrNull(OwnerBetaBox)),
        Group = new PerSideText(TextOrNull(GroupSessionBox), TextOrNull(GroupAlphaBox), TextOrNull(GroupBetaBox)),
        FileMode = new PerSideText(TextOrNull(FileModeSessionBox), TextOrNull(FileModeAlphaBox), TextOrNull(FileModeBetaBox)),
        DirectoryMode = new PerSideText(
            TextOrNull(DirectoryModeSessionBox), TextOrNull(DirectoryModeAlphaBox), TextOrNull(DirectoryModeBetaBox)),
        SymlinkMode = GetValue<SymlinkMode>(SymlinkModeCombo),
        WatchMode = new PerSide<WatchMode>(
            GetValue<WatchMode>(WatchModeSessionCombo), GetNullableValue<WatchMode>(WatchModeAlphaCombo),
            GetNullableValue<WatchMode>(WatchModeBetaCombo)),
        WatchPollingInterval = new PerSide<int>(
            IntOrNull(WatchPollingIntervalSessionBox), IntOrNull(WatchPollingIntervalAlphaBox), IntOrNull(WatchPollingIntervalBetaBox)),
        ProbeMode = new PerSide<ProbeMode>(
            GetValue<ProbeMode>(ProbeModeSessionCombo), GetNullableValue<ProbeMode>(ProbeModeAlphaCombo),
            GetNullableValue<ProbeMode>(ProbeModeBetaCombo)),
        ScanMode = new PerSide<ScanMode>(
            GetValue<ScanMode>(ScanModeSessionCombo), GetNullableValue<ScanMode>(ScanModeAlphaCombo),
            GetNullableValue<ScanMode>(ScanModeBetaCombo)),
        StageMode = new PerSide<StageMode>(
            GetValue<StageMode>(StageModeSessionCombo), GetNullableValue<StageMode>(StageModeAlphaCombo),
            GetNullableValue<StageMode>(StageModeBetaCombo)),
        MaxStagingFileSize = TextOrNull(MaxStagingFileSizeBox),
        MaxEntryCount = LongOrNull(MaxEntryCountBox),
        UnknownFlags = _unknownFlags.Select(f => new UnknownFlag(f.Text, f.Keep)).ToList(),
    };

    private void OnValidationFieldChanged(object sender, RoutedEventArgs e) => UpdateSaveEnabled();

    private void OnAlphaBrowseClick(object sender, RoutedEventArgs e) => ShowBrowseMenu((Button)sender, AlphaBox);

    private void OnBetaBrowseClick(object sender, RoutedEventArgs e) => ShowBrowseMenu((Button)sender, BetaBox);

    /// <summary>FR-18.5: opens a small menu offering "Browse local
    /// folder…" and "Browse SSH server…" next to the clicked field's
    /// browse button. Remote folder navigation/creation (Phase 3 of
    /// ENDPOINT_PATH_PICKER_PLAN.md) isn't implemented yet — see
    /// <see cref="SshFolderPickerWindow"/>.</summary>
    private void ShowBrowseMenu(Button button, TextBox target)
    {
        var localItem = new MenuItem { Header = "Browse local folder…" };
        localItem.Click += (_, _) => BrowseLocalFolder(target);

        var sshItem = new MenuItem { Header = "Browse SSH server…" };
        sshItem.Click += (_, _) => BrowseSshServer(target);

        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(localItem);
        menu.Items.Add(sshItem);
        menu.IsOpen = true;
    }

    private static void BrowseLocalFolder(TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Select folder" };
        if (Directory.Exists(target.Text.Trim()))
            dialog.InitialDirectory = target.Text.Trim();

        if (dialog.ShowDialog() == true)
            target.Text = dialog.FolderName;
    }

    private void BrowseSshServer(TextBox target)
    {
        var picker = new SshFolderPickerWindow(_sshServers, target.Text.Trim()) { Owner = this };
        if (picker.ShowDialog() == true)
            target.Text = picker.Result!;
    }

    /// <summary>FR-18.2/FR-18.4: Save is enabled only once Name/Alpha/Beta
    /// are all non-empty, Name has no whitespace (it's extracted up to the
    /// next space by FR-1.1's own regex — a name containing one would
    /// silently break re-parsing after Save), and Name doesn't collide
    /// with another session (case-sensitive, matching
    /// <c>SessionDefinitionLoader</c>'s ordinal lookup; the session's own
    /// original name, if editing, is excluded from this check).</summary>
    private void UpdateSaveEnabled()
    {
        var name = NameBox.Text.Trim();
        SaveButton.IsEnabled =
            name.Length > 0
            && !name.Any(char.IsWhiteSpace)
            && !_otherSessionNames.Contains(name)
            && AlphaBox.Text.Trim().Length > 0
            && BetaBox.Text.Trim().Length > 0;
    }

    /// <summary>Raised when Save is clicked and FR-18.4 validation already
    /// passed — <see cref="Result"/> is already built and available. The
    /// caller (App.xaml.cs) owns <c>SessionEditingService</c>'s actual
    /// `mutagen sync create`/file-write call; this window deliberately does
    /// NOT close itself here (no <c>DialogResult = true</c>) — it stays open
    /// with everything the user typed still in place until the caller
    /// confirms success via <see cref="CompleteSave"/>. Closing immediately
    /// and finding out afterwards that the CLI call failed would silently
    /// throw away everything the user just typed, forcing them to redo it
    /// from scratch — exactly what an early version of this window did.</summary>
    public event EventHandler? SaveRequested;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = BuildResult();
        _logger.LogInformation("User action: session edit window Save clicked ({Name})", Result.Name);
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the caller once the live `mutagen sync create`
    /// call (and file write) actually succeeded — only then does the window
    /// close with <c>DialogResult = true</c>.</summary>
    public void CompleteSave() => DialogResult = true;

    /// <summary>Toggles Save/Cancel while the caller's async operation is in
    /// flight (prevents a double-click re-entering it) — call with `false`
    /// again if the operation fails, so the user can fix the offending field
    /// and retry without having lost anything else they entered.</summary>
    public void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: session edit window Cancel clicked");
        DialogResult = false;
    }

    private sealed class ComboOption
    {
        public object? Value { get; init; }
        public string Display { get; init; } = "";
        public override string ToString() => Display;
    }

    private static void SetEnumItems<T>(ComboBox combo, bool includeBlank) where T : struct, Enum
    {
        var options = new List<ComboOption>();
        if (includeBlank)
            options.Add(new ComboOption { Value = null, Display = "" });
        options.AddRange(Enum.GetValues<T>().Select(v => new ComboOption { Value = v, Display = FormatEnum(v) }));
        combo.ItemsSource = options;
    }

    private static string FormatEnum<T>(T value) where T : struct, Enum => value switch
    {
        SyncMode m => m.ToFlagValue(),
        SymlinkMode m => m.ToFlagValue(),
        WatchMode m => m.ToFlagValue(),
        ProbeMode m => m.ToFlagValue(),
        ScanMode m => m.ToFlagValue(),
        StageMode m => m.ToFlagValue(),
        PermissionsMode m => m.ToFlagValue(),
        _ => value.ToString(),
    };

    private static void SelectValue(ComboBox combo, object? value) =>
        combo.SelectedItem = ((IEnumerable<ComboOption>)combo.ItemsSource).First(o => Equals(o.Value, value));

    private static T GetValue<T>(ComboBox combo) where T : struct, Enum => (T)((ComboOption)combo.SelectedItem).Value!;

    private static T? GetNullableValue<T>(ComboBox combo) where T : struct, Enum =>
        ((ComboOption)combo.SelectedItem).Value is T v ? v : null;

    private static string? TextOrNull(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    private static int? IntOrNull(TextBox box) => int.TryParse(box.Text.Trim(), out var value) ? value : null;

    private static long? LongOrNull(TextBox box) => long.TryParse(box.Text.Trim(), out var value) ? value : null;

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static string JoinLines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);
}

/// <summary>Plain mutable row for <see cref="SessionEditWindow"/>'s
/// "Unknown flags" list (FR-27.3) — <see cref="UnknownFlag"/> itself is an
/// immutable record (init-only <c>Keep</c>), which a two-way-bound
/// CheckBox can't write back to.</summary>
public sealed class UnknownFlagItem
{
    public string Text { get; set; } = "";
    public bool Keep { get; set; } = true;
}

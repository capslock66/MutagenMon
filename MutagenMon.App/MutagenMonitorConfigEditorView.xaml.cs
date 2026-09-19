using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Configuration;

namespace MutagenMon.App;

/// <summary>
/// Edits MutagenMon's own configuration file (<c>config_mutagenmon.json</c>,
/// see requirements/06-configuration-reference.md), as a structured form —
/// unlike <see cref="MutagenConfigEditorView"/>'s raw-text editor for
/// mutagen's own <c>~/.mutagen.yml</c>, this file's schema is fully owned by
/// MutagenMon (<see cref="MutagenMonOptions"/>), so every key gets its own
/// typed field instead of a free-text box. A structured form serializes
/// clean JSON on Save, which loses the shipped file's <c>#</c>-prefixed
/// comments for good the first time this tab saves — there is no portable
/// way to round-trip them through JSON, so each field carries the
/// documentation those comments held as a <c>ToolTip</c> instead
/// (see TABS_UI_PLAN.md).
///
/// Hosted as a tab in <see cref="StatusWindow"/>, like
/// <see cref="MutagenConfigEditorView"/> — "Reload config &amp; restart"
/// lives once, in the window's shared bottom toolbar, so this view only
/// owns Check/Save.
///
/// Unlike <c>~/.mutagen.yml</c>, `config_mutagenmon.json` is guaranteed to
/// already exist and parse by the time this tab can be shown — the whole
/// application fails to start otherwise (<c>App.OnStartup</c>) — so this
/// view doesn't need FR-30.4's "file doesn't exist yet" handling.
/// </summary>
public partial class MutagenMonitorConfigEditorView : UserControl
{
    private ILogger _logger = null!;
    private string _path = null!;

    /// <summary>The value last loaded/saved — carries through every field
    /// this form has no control for (currently just <c>DebugLevel</c>, a
    /// legacy no-op dial) unchanged on Save.</summary>
    private MutagenMonOptions _loadedOptions = null!;

    private readonly ObservableCollection<AutoResolveRule> _autoResolveRules = new();

    public MutagenMonitorConfigEditorView()
    {
        InitializeComponent();
        MinLogLevelCombo.ItemsSource = Enum.GetValues<LogLevel>();
        AutoResolveGrid.ItemsSource = _autoResolveRules;
    }

    public void Initialize(ILogger logger)
    {
        _logger = logger;
        _path = Path.Combine(AppContext.BaseDirectory, "config", "config_mutagenmon.json");

        try
        {
            _loadedOptions = ConfigLoader.Load(_path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load mutagen monitor config for editing");
            ShowValidationResult(new[] { $"Could not load '{_path}': {ex.Message}" });
            _loadedOptions = new MutagenMonOptions();
        }

        PopulateFromOptions(_loadedOptions);
    }

    private void PopulateFromOptions(MutagenMonOptions options)
    {
        StartEnabledCheck.IsChecked = options.StartEnabled;
        TrayTooltipBox.Text = options.TrayTooltip;

        MinLogLevelCombo.SelectedItem = options.MinLogLevel;
        LogPathBox.Text = options.LogPath;
        DebugExceptionsToConsoleCheck.IsChecked = options.DebugExceptionsToConsole;
        NotifyRestartConnectionCheck.IsChecked = options.NotifyRestartConnection;
        NotifyConflictsCheck.IsChecked = options.NotifyConflicts;
        NotifyAutoresolveCheck.IsChecked = options.NotifyAutoresolve;
        NotifyMutagenProfileUpdateCheck.IsChecked = options.NotifyMutagenProfileUpdate;

        MutagenPathBox.Text = options.MutagenPath;
        MutagenSessionsBatFileBox.Text = options.MutagenSessionsBatFile;
        MutagenProfileDirBox.Text = options.MutagenProfileDir;
        MergePathBox.Text = options.MergePath;
        ScpPathBox.Text = options.ScpPath;
        SshPathBox.Text = options.SshPath;

        MutagenPollPeriodMsBox.Text = options.MutagenPollPeriodMs.ToString();
        SessionMaxErrorsBox.Text = options.SessionMaxErrors.ToString();
        SessionMaxNoSessionBox.Text = options.SessionMaxNoSession.ToString();
        SessionMaxDuplicateBox.Text = options.SessionMaxDuplicate.ToString();
        MutagenProfileGraceSecondsBox.Text = options.MutagenProfileGraceSeconds.ToString();

        StatusMaxLagInfoBox.Text = options.StatusMaxLag.InfoSeconds.ToString();
        StatusMaxLagWarningBox.Text = options.StatusMaxLag.WarningSeconds.ToString();
        StatusMaxLagErrorBox.Text = options.StatusMaxLag.ErrorSeconds.ToString();
        StatusMaxLagRestartBox.Text = options.StatusMaxLag.RestartSeconds.ToString();

        AutoResolveHistoryAgeSecondsBox.Text = options.AutoResolveHistoryAgeSeconds.ToString();
        _autoResolveRules.Clear();
        foreach (var rule in options.AutoResolve)
            _autoResolveRules.Add(new AutoResolveRule { FilePath = rule.FilePath, Resolve = rule.Resolve });
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen monitor config editor Check clicked");
        ReadFromUi(out _, out var errors);
        ShowValidationResult(errors);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen monitor config editor Save clicked");

        ReadFromUi(out var options, out var errors);
        if (errors.Count > 0)
        {
            ShowValidationResult(errors);
            return;
        }

        try
        {
            ConfigLoader.Save(_path, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save mutagen monitor config file");
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not save its configuration file:\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ShowValidationResult(Array.Empty<string>());
        _loadedOptions = options;
        GenericMessageDialog.ShowInfo(
            Window.GetWindow(this), _logger, "MutagenMon",
            "The configuration was saved. \nThis does not affect the running monitor.\n"
            + "Use \"Reload config & restart\" (at the bottom of the window) to apply the change.");
    }

    private void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        _autoResolveRules.Add(new AutoResolveRule { FilePath = "", Resolve = "A wins" });
    }

    private void OnRemoveRuleClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AutoResolveRule rule)
            _autoResolveRules.Remove(rule);
    }

    private void OnMoveRuleUpClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AutoResolveRule rule)
            return;
        var index = _autoResolveRules.IndexOf(rule);
        if (index > 0)
            _autoResolveRules.Move(index, index - 1);
    }

    private void OnMoveRuleDownClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AutoResolveRule rule)
            return;
        var index = _autoResolveRules.IndexOf(rule);
        if (index >= 0 && index < _autoResolveRules.Count - 1)
            _autoResolveRules.Move(index, index + 1);
    }

    /// <summary>Builds a <see cref="MutagenMonOptions"/> from the form's
    /// current field values, and the list of validation errors found along
    /// the way (empty when everything parses). Fields this form has no
    /// control for (<c>DebugLevel</c>) are carried through from
    /// <see cref="_loadedOptions"/> unchanged.</summary>
    private void ReadFromUi(out MutagenMonOptions options, out List<string> errors)
    {
        errors = new List<string>();
        options = new MutagenMonOptions
        {
            DebugLevel = _loadedOptions.DebugLevel,
            StartEnabled = StartEnabledCheck.IsChecked == true,
            TrayTooltip = TrayTooltipBox.Text,
            MinLogLevel = (LogLevel)(MinLogLevelCombo.SelectedItem ?? LogLevel.Trace),
            LogPath = LogPathBox.Text,
            DebugExceptionsToConsole = DebugExceptionsToConsoleCheck.IsChecked == true,
            NotifyRestartConnection = NotifyRestartConnectionCheck.IsChecked == true,
            NotifyConflicts = NotifyConflictsCheck.IsChecked == true,
            NotifyAutoresolve = NotifyAutoresolveCheck.IsChecked == true,
            NotifyMutagenProfileUpdate = NotifyMutagenProfileUpdateCheck.IsChecked == true,
            MutagenPath = MutagenPathBox.Text,
            MutagenSessionsBatFile = MutagenSessionsBatFileBox.Text,
            MutagenProfileDir = MutagenProfileDirBox.Text,
            MergePath = MergePathBox.Text,
            ScpPath = ScpPathBox.Text,
            SshPath = SshPathBox.Text,
            MutagenPollPeriodMs = ParseInt(MutagenPollPeriodMsBox, "Poll period", errors),
            SessionMaxErrors = ParseInt(SessionMaxErrorsBox, "Max 'connecting' polls", errors),
            SessionMaxNoSession = ParseInt(SessionMaxNoSessionBox, "Max 'no session' polls", errors),
            SessionMaxDuplicate = ParseInt(SessionMaxDuplicateBox, "Max 'duplicate' polls", errors),
            MutagenProfileGraceSeconds = ParseInt(MutagenProfileGraceSecondsBox, "Profile update grace", errors),
            AutoResolveHistoryAgeSeconds = ParseInt(AutoResolveHistoryAgeSecondsBox, "Auto-resolve history age", errors),
            StatusMaxLag = new StatusMaxLagOptions
            {
                InfoSeconds = ParseInt(StatusMaxLagInfoBox, "Status max lag: Info", errors),
                WarningSeconds = ParseInt(StatusMaxLagWarningBox, "Status max lag: Warning", errors),
                ErrorSeconds = ParseInt(StatusMaxLagErrorBox, "Status max lag: Error", errors),
                RestartSeconds = ParseInt(StatusMaxLagRestartBox, "Status max lag: Restart", errors),
            },
            AutoResolve = _autoResolveRules.Select(r => new AutoResolveRule { FilePath = r.FilePath, Resolve = r.Resolve }).ToList(),
        };

        for (var i = 0; i < options.AutoResolve.Count; i++)
        {
            var rule = options.AutoResolve[i];
            if (rule.Resolve != "A wins" && rule.Resolve != "B wins")
                errors.Add($"Auto-resolve rule {i + 1}: 'Resolve' must be 'A wins' or 'B wins'.");
            try
            {
                _ = new Regex(rule.FilePath);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Auto-resolve rule {i + 1}: invalid regular expression — {ex.Message}");
            }
        }
    }

    private static int ParseInt(TextBox box, string fieldLabel, List<string> errors)
    {
        if (int.TryParse(box.Text, out var value))
            return value;
        errors.Add($"'{fieldLabel}' must be a whole number.");
        return 0;
    }

    private void ShowValidationResult(IReadOnlyList<string> errors)
    {
        ValidationErrorText.Text = string.Join(Environment.NewLine, errors);
        ValidationErrorText.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

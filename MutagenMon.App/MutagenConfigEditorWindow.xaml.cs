using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Configuration;

namespace MutagenMon.App;

/// <summary>
/// Edits mutagen's own global configuration file
/// (<c>%USERPROFILE%\.mutagen.yml</c>, FR-29 through FR-33,
/// requirements/08-mutagen-config-editor-requirements.md) — a plain
/// text/YAML editor, unlike <see cref="SessionEditWindow"/>'s field-by-field
/// form, since this file's structure isn't owned by MutagenMon at all.
/// Reads and writes the file directly (<see cref="MutagenConfigFile"/>)
/// instead of raising a save-request event for the caller to fulfil (unlike
/// <see cref="SessionEditWindow.SaveRequested"/>): saving here is a plain,
/// synchronous local file write with no external `mutagen` CLI call that
/// could fail independently of it, so there is no async operation for a
/// caller to own — the window can validate, write, and show the result in
/// one step, catching a write failure itself instead of delegating that
/// back out. Unlike <see cref="SessionEditWindow"/>, Save does NOT close
/// the window (FR-33 needs the window to stay open afterwards so "Reload
/// config &amp; restart" is actually clickable) — only "Close" does.
/// </summary>
public partial class MutagenConfigEditorWindow : Window
{
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly Encoding _encoding;

    /// <summary>Whether the file existed on disk as of the last successful
    /// load/save — starts at whatever <see cref="MutagenConfigFile.Load"/>
    /// found (FR-30.4), and becomes true after the first successful Save
    /// creates it. Drives the FR-32.4 "not applied live" note: shown after
    /// a Save that overwrote a file that was already there, not after the
    /// one that created it (nothing "already running" to warn about yet).</summary>
    private bool _fileExisted;

    /// <summary>The content as of the last load/save, to detect whether the
    /// text actually changed by the time Save is clicked (FR-33: the
    /// reload button only enables for a Save that persisted a real
    /// change).</summary>
    private string _lastSavedContent;

    /// <summary>Raised when the user clicks "Reload config &amp; restart"
    /// (FR-33) — handled by App.xaml.cs, which reuses the exact same
    /// FR-7.1 reload pathway as the status view's own toolbar button
    /// (terminates and recreates every session, which is what actually
    /// applies a `~/.mutagen.yml` change — restarting the `mutagen` daemon
    /// process itself, an earlier version of this button, was found by
    /// manual testing to have no effect on already-running sessions).</summary>
    public event EventHandler? ReloadConfigRequested;

    public MutagenConfigEditorWindow(ILogger logger)
    {
        InitializeComponent();
        _logger = logger;
        _path = MutagenConfigFile.ResolvePath();

        var loaded = MutagenConfigFile.Load(_path);
        EditorBox.Text = loaded.Content;
        _encoding = loaded.Encoding;
        _fileExisted = loaded.FileExisted;
        _lastSavedContent = loaded.Content;
        NewFileNoticeText.Visibility = _fileExisted ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen config editor Check clicked");
        ShowValidationResult(MutagenYamlValidator.Validate(EditorBox.Text));
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen config editor Save clicked");

        var errors = MutagenYamlValidator.Validate(EditorBox.Text);
        if (errors.Count > 0)
        {
            ShowValidationResult(errors);
            return;
        }

        var isModified = EditorBox.Text != _lastSavedContent;
        var overwritesExistingFile = _fileExisted;

        try
        {
            MutagenConfigFile.Save(_path, EditorBox.Text, _encoding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save mutagen config file");
            MessageBox.Show(
                this, $"MutagenMon could not save the mutagen config file:\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        NewFileNoticeText.Visibility = Visibility.Collapsed;
        _fileExisted = true;
        _lastSavedContent = EditorBox.Text;

        if (isModified)
            ReloadConfigButton.IsEnabled = true;

        if (overwritesExistingFile)
            GenericMessageDialog.ShowInfo(
                this, _logger, "MutagenMon",
                "The mutagen config file was saved. This does not affect already-running "
                + "sessions — use \"Reload config & restart\" (below) to apply the change "
                + "to them.");
    }

    private void OnReloadConfigClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen config editor Reload config & restart clicked");
        // No busy/re-enable toggling here (unlike the old daemon-restart
        // button): this triggers the same fire-and-forget FR-7.1 pathway as
        // the status view's own "Reload config" button, which has no
        // synchronous completion signal to wait on — disabling once and
        // leaving it disabled is enough to stop a double-click from
        // queuing a second reload.
        ReloadConfigButton.IsEnabled = false;
        ReloadConfigRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowValidationResult(IReadOnlyList<string> errors)
    {
        ValidationErrorText.Text = string.Join(Environment.NewLine, errors);
        ValidationErrorText.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen config editor Close clicked");
        DialogResult = false;
    }
}

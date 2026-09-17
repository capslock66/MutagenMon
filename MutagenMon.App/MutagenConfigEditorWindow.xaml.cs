using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace MutagenMon.App;

/// <summary>
/// Edits mutagen's own global configuration file
/// (<c>%USERPROFILE%\.mutagen.yml</c>, FR-29 through FR-33,
/// requirements/08-mutagen-config-editor-requirements.md) — a plain
/// text/YAML editor, unlike <see cref="SessionEditWindow"/>'s field-by-field
/// form, since this file's structure isn't owned by MutagenMon at all.
/// Reads and writes the file directly (its own <see cref="LoadConfigFile"/>/
/// <see cref="SaveConfigFile"/> — distinct from MutagenMon's own
/// <see cref="App.LoadConfig"/>-loaded
/// <c>config_mutagenmon.json</c>, and from mutagen's
/// <c>%USERPROFILE%\.mutagen</c> data directory) instead of raising a
/// save-request event for the caller to fulfil (unlike
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
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger;
    private readonly string _path;
    private readonly Encoding _encoding;

    /// <summary>Whether the file existed on disk as of the last successful
    /// load/save — starts at whatever <see cref="LoadConfigFile"/>
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
        _path = ResolveConfigPath();

        var loaded = LoadConfigFile(_path);
        EditorBox.Text = loaded.Content;
        _encoding = loaded.Encoding;
        _fileExisted = loaded.FileExisted;
        _lastSavedContent = loaded.Content;
        NewFileNoticeText.Visibility = _fileExisted ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen config editor Check clicked");
        ShowValidationResult(ValidateYaml(EditorBox.Text));
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: mutagen config editor Save clicked");

        var errors = ValidateYaml(EditorBox.Text);
        if (errors.Count > 0)
        {
            ShowValidationResult(errors);
            return;
        }

        var isModified = EditorBox.Text != _lastSavedContent;
        var overwritesExistingFile = _fileExisted;

        try
        {
            SaveConfigFile(_path, EditorBox.Text, _encoding);
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

    /// <summary>Empty on valid YAML; otherwise the parser's error message,
    /// including line/column when available. A single document can only
    /// surface one syntax error at a time (parsing stops there), so this is
    /// at most a one-element list — kept as a list so a caller iterating it
    /// doesn't need to change if that ever stops being true.
    /// Catches any exception, not just YamlDotNet's own
    /// <c>YamlException</c>: some malformed inputs (e.g. an unterminated
    /// flow sequence) trip an internal scanner assumption and surface as a
    /// plain <see cref="InvalidOperationException"/> instead — this is
    /// user-typed free text being actively edited, so any parse failure
    /// must turn into an FR-31 error message, never an unhandled
    /// exception.</summary>
    private static IReadOnlyList<string> ValidateYaml(string yamlText)
    {
        try
        {
            new YamlStream().Load(new StringReader(yamlText));
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            return new[] { ex.Message };
        }
    }

    /// <summary>Fixed path this feature always targets (FR-30.2) — not
    /// user-configurable.</summary>
    private static string ResolveConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mutagen.yml");

    /// <summary><see cref="ConfigFileLoadResult.Content"/> is empty and
    /// <see cref="ConfigFileLoadResult.FileExisted"/> is false when the file
    /// wasn't there yet (FR-30.4). <see cref="ConfigFileLoadResult.Encoding"/>
    /// is the file's own detected encoding (FR-30.3), or UTF-8 without BOM
    /// for a not-yet-existing file (FR-32.2).</summary>
    private readonly record struct ConfigFileLoadResult(string Content, Encoding Encoding, bool FileExisted);

    private static ConfigFileLoadResult LoadConfigFile(string path)
    {
        if (!File.Exists(path))
            return new ConfigFileLoadResult("", Utf8NoBom, FileExisted: false);

        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new ConfigFileLoadResult(content, encoding, FileExisted: true);
    }

    /// <summary>Overwrites (or creates) <paramref name="path"/> with
    /// <paramref name="content"/>, using <paramref name="encoding"/> —
    /// normally the one <see cref="LoadConfigFile"/> detected, so a re-save
    /// preserves the file's original encoding (including BOM
    /// presence/absence) rather than silently normalizing it (FR-32.2). No
    /// locking or external-change detection: last write wins
    /// (FR-32.3).</summary>
    private static void SaveConfigFile(string path, string content, Encoding encoding) =>
        File.WriteAllText(path, content, encoding);

    /// <summary>Detects the encoding from a leading byte-order mark, falling
    /// back to UTF-8 without BOM when none is present. Deliberately not
    /// using <c>StreamReader</c>'s own BOM detection here: its
    /// <c>CurrentEncoding</c> can't reliably distinguish "no BOM was found,
    /// fell back to the caller-supplied default" from "the BOM found happens
    /// to match that same default", which matters here because
    /// <see cref="Encoding.UTF8"/>'s preamble (used for the BOM case) is
    /// non-empty while the no-BOM fallback must have none, and getting that
    /// wrong would add or drop a BOM the original file didn't have.</summary>
    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return Encoding.UTF8;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return Encoding.Unicode;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return Utf8NoBom;
    }
}

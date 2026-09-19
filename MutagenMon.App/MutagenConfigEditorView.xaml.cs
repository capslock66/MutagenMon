using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
/// <c>%USERPROFILE%\.mutagen</c> data directory).
///
/// Hosted as a tab in <see cref="StatusWindow"/> rather than a standalone
/// modal dialog: "Reload config &amp; restart" now lives once, in the
/// window's shared bottom toolbar, so this view only owns Check/Save — it
/// no longer gates that button's enabled state on whether Save persisted a
/// real change (FR-33's original per-editor gating doesn't apply to a
/// button shared by every tab). The file is read once, when this control is
/// constructed (i.e. the first time the status window is shown) — unlike
/// the old modal, which re-read it fresh every time it was opened, an
/// external edit made while the status window is already open/hidden won't
/// be picked up until the app restarts.
/// </summary>
public partial class MutagenConfigEditorView : UserControl
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private ILogger _logger = null!;
    private string _path = null!;
    private Encoding _encoding = Utf8NoBom;

    /// <summary>Whether the file existed on disk as of the last successful
    /// load/save (FR-30.4) — becomes true after the first successful Save
    /// creates it. Drives the FR-32.4 "not applied live" note: shown after a
    /// Save that overwrote a file that was already there, not after the one
    /// that created it (nothing "already running" to warn about yet).</summary>
    private bool _fileExisted;

    public MutagenConfigEditorView()
    {
        InitializeComponent();
    }

    public void Initialize(ILogger logger)
    {
        _logger = logger;
        _path = ResolveConfigPath();

        var loaded = LoadConfigFile(_path);
        EditorBox.Text = loaded.Content;
        _encoding = loaded.Encoding;
        _fileExisted = loaded.FileExisted;
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

        var overwritesExistingFile = _fileExisted;

        try
        {
            SaveConfigFile(_path, EditorBox.Text, _encoding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save mutagen config file");
            MessageBox.Show(
                Window.GetWindow(this), $"MutagenMon could not save the mutagen config file:\n\n{ex.Message}",
                "MutagenMon", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        NewFileNoticeText.Visibility = Visibility.Collapsed;
        _fileExisted = true;

        if (overwritesExistingFile)
            GenericMessageDialog.ShowInfo(
                Window.GetWindow(this), _logger, "MutagenMon",
                "The mutagen config file was saved. This does not affect already-running "
                + "sessions — use \"Reload config & restart\" (at the bottom of the window) "
                + "to apply the change to them.");
    }

    private void ShowValidationResult(IReadOnlyList<string> errors)
    {
        ValidationErrorText.Text = string.Join(Environment.NewLine, errors);
        ValidationErrorText.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MutagenMon.Core.Configuration;

/// <summary>
/// Reads and writes <c>config_mutagenmon.json</c> (see
/// requirements/06-configuration-reference.md). The shipped file is JSON
/// with whole-line <c>#</c> comments (never inline trailing ones), which
/// aren't valid JSON and must be stripped before parsing — a legacy
/// convention this app tolerates on read but never reproduces on write:
/// <see cref="Save"/> always emits plain JSON, so a file saved through this
/// class loses any <c>#</c> comments it had for good (no portable way to
/// round-trip them through JSON).
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MutagenMonOptions Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Explicit %USERPROFILE% expansion for
    /// <see cref="MutagenMonOptions.MutagenProfileDir"/>;
    /// <see cref="Environment.ExpandEnvironmentVariables(string)"/> is a
    /// no-op for text with no %...% placeholders, so this is safe to always
    /// apply.</summary>
    public static MutagenMonOptions Parse(string rawTextWithComments)
    {
        var cleaned = StripCommentLines(rawTextWithComments);
        var options = JsonSerializer.Deserialize<MutagenMonOptions>(cleaned, JsonOptions)
            ?? throw new InvalidDataException("Config file parsed to a null document.");
        options.MutagenProfileDir = Environment.ExpandEnvironmentVariables(options.MutagenProfileDir);
        return options;
    }

    public static string Serialize(MutagenMonOptions options) => JsonSerializer.Serialize(options, JsonOptions);

    public static void Save(string path, MutagenMonOptions options) => File.WriteAllText(path, Serialize(options));

    /// <summary>Updates only <see cref="MutagenMonOptions.StatusWindowWidth"/>
    /// and <see cref="MutagenMonOptions.StatusWindowHeight"/> in the file,
    /// leaving everything else — including the whole-line <c>#</c>
    /// comments that <see cref="Save"/> would drop — untouched. The
    /// application calls this on its own at each window resize, so it must
    /// not rewrite the user's file.</summary>
    public static void SaveWindowSize(string path, int width, int height) =>
        File.WriteAllText(path, ApplyWindowSize(File.ReadAllText(path), width, height));

    internal static string ApplyWindowSize(string text, int width, int height)
    {
        text = SetIntegerKey(text, nameof(MutagenMonOptions.StatusWindowWidth), width);
        return SetIntegerKey(text, nameof(MutagenMonOptions.StatusWindowHeight), height);
    }

    /// <summary>Replaces the value of an existing integer key, or inserts
    /// the key right after the opening brace of the root object.</summary>
    private static string SetIntegerKey(string text, string key, int value)
    {
        // Matches a non-commented `"<key>": <number>` line; group 1 is everything up to the number, which is replaced.
        var existing = new Regex($"""(?m)^(\s*"{key}"\s*:\s*)-?\d+(\.\d+)?""");
        if (existing.IsMatch(text))
            return existing.Replace(text, m_ => m_.Groups[1].Value + value, 1);

        return InsertKey(text, key, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Inserts <c>"key": jsonValue</c> right after the opening brace
    /// of the root object, leaving every other line (comments included)
    /// untouched. <paramref name="jsonValue"/> is already valid JSON text.</summary>
    private static string InsertKey(string text, string key, string jsonValue)
    {
        // Matches up to and including the first "{" of a line that is not a "#" comment line, i.e. the root object's
        // opening brace; no capture groups — the end of the match (Index + Length) is the insertion point.
        var open = Regex.Match(text, @"(?m)^[^#\r\n]*?\{");
        if (!open.Success)
            throw new InvalidDataException($"Config file has no root object to add the key '{key}' to.");

        var afterOpen = open.Index + open.Length;
        var isEmptyObject = text.AsSpan(afterOpen).TrimStart().StartsWith("}");
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        return text.Insert(afterOpen, $"{lineEnding}  \"{key}\": {jsonValue}{(isEmptyObject ? "" : ",")}");
    }

    /// <summary>FR-63.1: adds to the file, with their default value, every
    /// halted-session key (<c>HaltedOneSidedRootEmptying*</c>) that is absent
    /// from it, and returns the names of the keys it added (empty when the
    /// file already had them all, in which case it is not rewritten). The
    /// file is edited as text, like <see cref="SaveWindowSize"/>, so the
    /// <c>#</c> comments and every other key are kept; a key already present
    /// is never modified. The caller decides what to do if the file cannot be
    /// written.</summary>
    public static IReadOnlyList<string> EnsureHaltedKeys(string path)
    {
        var updated = ApplyMissingHaltedKeys(File.ReadAllText(path), out var added);
        if (added.Count > 0)
            File.WriteAllText(path, updated);
        return added;
    }

    internal static string ApplyMissingHaltedKeys(string text, out IReadOnlyList<string> addedKeys)
    {
        var defaults = new MutagenMonOptions();
        (string Key, string JsonValue)[] keys =
        [
            (nameof(MutagenMonOptions.HaltedOneSidedRootEmptying), JsonSerializer.Serialize(defaults.HaltedOneSidedRootEmptying)),
            (nameof(MutagenMonOptions.HaltedOneSidedRootEmptyingAutoFix), defaults.HaltedOneSidedRootEmptyingAutoFix ? "true" : "false"),
            (nameof(MutagenMonOptions.HaltedOneSidedRootEmptyingCooldownSeconds),
                defaults.HaltedOneSidedRootEmptyingCooldownSeconds.ToString(CultureInfo.InvariantCulture)),
        ];

        var added = new List<string>();
        // Each insertion goes right after the opening brace, which reverses the order of the added keys: walking the
        // list backwards leaves them in the order declared above.
        for (var i = keys.Length - 1; i >= 0; i--)
        {
            if (KeyExists(text, keys[i].Key))
                continue;
            text = InsertKey(text, keys[i].Key, keys[i].JsonValue);
            added.Insert(0, keys[i].Key);
        }

        addedKeys = added;
        return text;
    }

    private static bool KeyExists(string text, string key)
    {
        // Matches the quoted key followed by a colon, at the start of a line or right after a "{" or a "," (so a
        // minified file is handled too). Whole-line "#" comments are stripped first, so a commented-out key never
        // matches. One capture group (the separator before the key), not used.
        var existing = new Regex($$"""(?m)(^|[{,])\s*"{{key}}"\s*:""");
        return existing.IsMatch(StripCommentLines(text));
    }

    private static string StripCommentLines(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith('#'))
                continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}

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

        var open = Regex.Match(text, @"(?m)^[^#\r\n]*?\{");
        if (!open.Success)
            throw new InvalidDataException("Config file has no root object to add the window size to.");

        var afterOpen = open.Index + open.Length;
        var isEmptyObject = text.AsSpan(afterOpen).TrimStart().StartsWith("}");
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        return text.Insert(afterOpen, $"{lineEnding}  \"{key}\": {value}{(isEmptyObject ? "" : ",")}");
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

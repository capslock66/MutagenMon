using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

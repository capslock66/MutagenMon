using System.Text;
using System.Text.Json;

namespace MutagenMon.Core.Configuration;

/// <summary>
/// Loads the app's configuration. The shipped config file is
/// JSON with whole-line '#' comments (never inline trailing ones), stripped
/// before parsing.
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
    };

    public static MutagenMonOptions ParseText(string rawTextWithComments)
    {
        var cleaned = StripCommentLines(rawTextWithComments);
        var options = JsonSerializer.Deserialize<MutagenMonOptions>(cleaned, JsonOptions)
            ?? throw new InvalidDataException("Config file parsed to a null document.");

        // Explicit %USERPROFILE% expansion for
        // MUTAGEN_PROFILE_DIR; ExpandEnvironmentVariables is a no-op for text with
        // no %...% placeholders, so this is safe to always apply.
        options.MutagenProfileDir = Environment.ExpandEnvironmentVariables(options.MutagenProfileDir);

        return options;
    }

    public static MutagenMonOptions Load(string path) => ParseText(File.ReadAllText(path));

    private static string StripCommentLines(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}

using System.Text.RegularExpressions;

namespace MutagenMon.Core.Sessions;

/// <summary>
/// Parses session
/// names out of a mutagen-create.bat-style file (one
/// `mutagen sync create ... --name=&lt;name&gt; ...` line per session, `rem `-prefixed
/// lines skipped). Last definition wins on a duplicate name,
/// with the duplicate flagged for the caller (FR-1.2) instead of popping a
/// dialog itself.
///
/// Also reads the per-session `@halted-auto-fix` directive lines (FR-64):
/// `rem sync --name=&lt;session&gt; : @halted-auto-fix: &lt;true|false&gt;`. They are
/// `rem ` lines, so the session lookup above never mistakes them for a
/// session.
/// </summary>
public static partial class SessionDefinitionLoader
{
    [GeneratedRegex(@"--name=(\S+)")]
    private static partial Regex NameRegex();

    // Matches a whole `@halted-auto-fix` directive line, e.g. "rem sync --name=temp : @halted-auto-fix: true"
    // (keyword case-insensitive, spaces around the colons optional). Groups: 1 = the session name, 2 = the raw value
    // ("true"/"false" when valid, anything else is reported as invalid by the caller) — access via match.Groups[n].Value.
    [GeneratedRegex(@"^\s*rem\s+sync\s+--name=(\S+)\s*:\s*@halted-auto-fix\s*:\s*(\S+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HaltedAutoFixDirectiveRegex();

    public static SessionDefinitionLoadResult ParseLines(IEnumerable<string> lines)
    {
        var allLines = lines as IReadOnlyList<string> ?? lines.ToList();
        var sessions = new Dictionary<string, SessionDefinition>();
        var duplicates = new List<string>();

        foreach (var rawLine in allLines)
        {
            if (!TryExtractName(rawLine, out var line, out var name))
                continue;

            if (sessions.ContainsKey(name))
                duplicates.Add(name);
            sessions[name] = new SessionDefinition(name, line);
        }

        var directives = ParseHaltedAutoFixDirectives(allLines, sessions.Keys.ToHashSet(StringComparer.Ordinal), out var warnings);
        var definitions = sessions.Values
            .Select(s_ => directives.TryGetValue(s_.Name, out var flag) ? s_ with { HaltedAutoFix = flag } : s_)
            .ToArray();
        return new SessionDefinitionLoadResult(definitions, duplicates, directives, warnings);
    }

    public static SessionDefinitionLoadResult ParseFile(string path) => ParseLines(File.ReadAllLines(path));

    /// <summary>FR-64.4: collects the `@halted-auto-fix` directives of a
    /// sessions file, wherever they are in it. The last directive of a
    /// session wins; an invalid value, or a directive naming a session that
    /// is not an active one in <paramref name="sessionNames"/> (unknown or
    /// `rem`-disabled), is ignored and reported in <paramref name="warnings"/>.</summary>
    internal static IReadOnlyDictionary<string, bool> ParseHaltedAutoFixDirectives(
        IEnumerable<string> lines, IReadOnlySet<string> sessionNames, out IReadOnlyList<string> warnings)
    {
        var directives = new Dictionary<string, bool>(StringComparer.Ordinal);
        var warningList = new List<string>();

        foreach (var line in lines)
        {
            if (!TryParseHaltedAutoFixDirective(line, out var name, out var rawValue))
                continue;

            if (!bool.TryParse(rawValue, out var value))
                warningList.Add($"Ignored the @halted-auto-fix directive of session '{name}': '{rawValue}' is not 'true' or 'false'.");
            else if (!sessionNames.Contains(name))
                warningList.Add($"Ignored the @halted-auto-fix directive of '{name}': no such active session.");
            else
                directives[name] = value;
        }

        warnings = warningList;
        return directives;
    }

    /// <summary>Shared with <c>SessionEditingService</c> so "is this line a
    /// directive" is decided in exactly one place. Only recognises the line
    /// shape; <paramref name="rawValue"/> is not validated here.</summary>
    internal static bool TryParseHaltedAutoFixDirective(string line, out string name, out string rawValue)
    {
        var match = HaltedAutoFixDirectiveRegex().Match(line);
        name = match.Success ? match.Groups[1].Value : "";
        rawValue = match.Success ? match.Groups[2].Value : "";
        return match.Success;
    }

    internal static string FormatHaltedAutoFixDirective(string name, bool value) =>
        $"rem sync --name={name} : @halted-auto-fix: {(value ? "true" : "false")}";

    /// <summary>Shared with <c>SessionEditingService</c> so "which line
    /// is session X" is decided in exactly one place. Returns false (a
    /// `rem `-prefixed line, or one with no/empty `--name=`) for anything
    /// that isn't an active session line — <paramref name="trimmedLine"/>
    /// and <paramref name="name"/> are only meaningful when it returns
    /// true.</summary>
    internal static bool TryExtractName(string rawLine, out string trimmedLine, out string name)
    {
        trimmedLine = rawLine.Trim();
        name = "";
        if (trimmedLine.StartsWith("rem ", StringComparison.Ordinal))
            return false;

        // A directive written as "REM ..." (or with a tab) is not caught by the check above but contains "--name=".
        if (HaltedAutoFixDirectiveRegex().IsMatch(trimmedLine))
            return false;

        var match = NameRegex().Match(trimmedLine);
        if (!match.Success || match.Groups[1].Value.Length == 0)
            return false;

        name = match.Groups[1].Value;
        return true;
    }
}

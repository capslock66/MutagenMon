using System.Text.RegularExpressions;

namespace MutagenMon.Core.Sessions;

/// <summary>
/// Parses session
/// names out of a mutagen-create.bat-style file (one
/// `mutagen sync create ... --name=&lt;name&gt; ...` line per session, `rem `-prefixed
/// lines skipped). Last definition wins on a duplicate name,
/// with the duplicate flagged for the caller (FR-1.2) instead of popping a
/// dialog itself.
/// </summary>
public static partial class SessionDefinitionLoader
{
    [GeneratedRegex(@"--name=(\S+)")]
    private static partial Regex NameRegex();

    public static SessionDefinitionLoadResult ParseLines(IEnumerable<string> lines)
    {
        var sessions = new Dictionary<string, SessionDefinition>();
        var duplicates = new List<string>();

        foreach (var rawLine in lines)
        {
            if (!TryExtractName(rawLine, out var line, out var name))
                continue;

            if (sessions.ContainsKey(name))
                duplicates.Add(name);
            sessions[name] = new SessionDefinition(name, line);
        }

        return new SessionDefinitionLoadResult(sessions.Values.ToArray(), duplicates);
    }

    public static SessionDefinitionLoadResult ParseFile(string path) => ParseLines(File.ReadAllLines(path));

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

        var match = NameRegex().Match(trimmedLine);
        if (!match.Success || match.Groups[1].Value.Length == 0)
            return false;

        name = match.Groups[1].Value;
        return true;
    }
}

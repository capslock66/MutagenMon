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
            var line = rawLine.Trim();
            if (line.StartsWith("rem ", StringComparison.Ordinal)) continue;

            var match = NameRegex().Match(line);
            if (!match.Success) continue;

            var name = match.Groups[1].Value;
            if (name.Length == 0) continue;

            if (sessions.ContainsKey(name)) duplicates.Add(name);
            sessions[name] = new SessionDefinition(name, line);
        }

        return new SessionDefinitionLoadResult(sessions.Values.ToArray(), duplicates);
    }

    public static SessionDefinitionLoadResult ParseFile(string path) => ParseLines(File.ReadAllLines(path));
}

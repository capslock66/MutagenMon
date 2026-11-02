using System.Text.RegularExpressions;

namespace MutagenMon.Core.Scripts;

/// <summary>
/// Parses the ini-like scripts file (FR-48): `[name]` section headers, body
/// lines up to the next header, `# @key: value` directives. Pure — a
/// missing or unreadable file is the caller's concern (FR-49.2).
/// </summary>
public static partial class ScriptFileParser
{
    // FR-48.1 / FR-48.3: the first character after '[' cannot be a space or
    // '[', so bash tests such as `[ -f x ]` or `[[ -n "$x" ]]` never match.
    [GeneratedRegex(@"^\[([A-Za-z0-9_.\-][A-Za-z0-9_.\- ]*)\]$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^#\s*@([A-Za-z\-]+)\s*:\s*(.*)$")]
    private static partial Regex DirectiveRegex();

    public static IReadOnlyList<ScriptDefinition> ParseFile(string path) => ParseLines(File.ReadAllLines(path));

    public static IReadOnlyList<ScriptDefinition> ParseLines(IEnumerable<string> lines)
    {
        var sections = new List<(string Name, List<string> Body)>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var header = HeaderRegex().Match(line.Trim());
            if (header.Success)
                sections.Add((header.Groups[1].Value, new List<string>()));
            else if (sections.Count > 0)
                sections[^1].Body.Add(line);
        }

        var duplicateNames = sections
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        return sections.Select(s => Build(s.Name, s.Body, duplicateNames.Contains(s.Name))).ToArray();
    }

    private static ScriptDefinition Build(string name, List<string> bodyLines, bool isDuplicate)
    {
        TrimBlankEdges(bodyLines);
        var body = string.Join('\n', bodyLines);

        string? host = null;
        var description = "";
        var stopOnError = true;
        var confirm = false;
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in bodyLines)
        {
            var match = DirectiveRegex().Match(line.Trim());
            if (!match.Success)
                continue;

            var key = match.Groups[1].Value.ToLowerInvariant();
            var value = match.Groups[2].Value.Trim();
            if (!seen.Add(key))
                continue; // first occurrence wins

            switch (key)
            {
                case "host":
                    host = value.Length == 0 ? null : value;
                    break;
                case "description":
                    description = value;
                    break;
                case "stop-on-error":
                    if (TryParseBool(value, out var stop))
                        stopOnError = stop;
                    else
                        errors.Add($"@stop-on-error must be 'true' or 'false' (got '{value}')");
                    break;
                case "confirm":
                    if (TryParseBool(value, out var ask))
                        confirm = ask;
                    else
                        errors.Add($"@confirm must be 'true' or 'false' (got '{value}')");
                    break;
            }
        }

        if (host is null)
            errors.Add("missing @host directive");
        if (isDuplicate)
            errors.Add($"duplicate script name '{name}'");

        var validationError = errors.Count == 0 ? null : string.Join("; ", errors);
        return new ScriptDefinition(name, host, description, stopOnError, confirm, body, validationError);
    }

    private static bool TryParseBool(string value, out bool result)
    {
        result = false;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            result = true;
        else if (!value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static void TrimBlankEdges(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
    }
}

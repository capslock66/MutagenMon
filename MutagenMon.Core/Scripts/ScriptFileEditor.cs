using System.Text;

namespace MutagenMon.Core.Scripts;

/// <summary>
/// Rewrites the body of one script in the scripts file, leaving the rest
/// of the file (other scripts, comments before the first header, blank
/// lines between sections) untouched. Keeps the file's encoding and line
/// ending style.
/// </summary>
public static class ScriptFileEditor
{
    /// <summary>Replaces the body of <paramref name="scriptName"/> in the
    /// file. Throws <see cref="InvalidOperationException"/> when the name
    /// matches no section or several (duplicates, FR-49.2) — the section to
    /// rewrite would be ambiguous.</summary>
    public static void ReplaceBody(string path, string scriptName, string newBody)
    {
        string text;
        Encoding encoding;
        using (var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        File.WriteAllText(path, ReplaceBodyInText(text, scriptName, newBody), encoding);
    }

    internal static string ReplaceBodyInText(string text, string scriptName, string newBody)
    {
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var headerIndexes = new List<int>();
        var matches = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!ScriptFileParser.TryGetSectionName(lines[i], out var name))
                continue;

            headerIndexes.Add(i);
            if (name == scriptName)
                matches.Add(i);
        }

        if (matches.Count != 1)
            throw new InvalidOperationException(
                matches.Count == 0
                    ? $"Script '{scriptName}' was not found in the file."
                    : $"Script name '{scriptName}' appears {matches.Count} times in the file.");

        var bodyStart = matches[0] + 1;
        var sectionEnd = headerIndexes.FirstOrDefault(index => index > matches[0], lines.Count);

        // Blank lines closing the section (the separator before the next
        // header, or the file's final newline) are not part of the body.
        var bodyEnd = sectionEnd;
        while (bodyEnd > bodyStart && string.IsNullOrWhiteSpace(lines[bodyEnd - 1]))
            bodyEnd--;

        var newLines = newBody.Replace("\r\n", "\n").Split('\n').ToList();
        while (newLines.Count > 0 && string.IsNullOrWhiteSpace(newLines[0]))
            newLines.RemoveAt(0);
        while (newLines.Count > 0 && string.IsNullOrWhiteSpace(newLines[^1]))
            newLines.RemoveAt(newLines.Count - 1);

        lines.RemoveRange(bodyStart, bodyEnd - bodyStart);
        lines.InsertRange(bodyStart, newLines);
        return string.Join(lineEnding, lines);
    }
}

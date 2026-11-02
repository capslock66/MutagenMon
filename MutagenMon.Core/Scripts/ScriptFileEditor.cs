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
    /// file and, when <paramref name="newName"/> is given and different,
    /// renames its section. Throws <see cref="InvalidOperationException"/>
    /// when the name matches no section or several (duplicates, FR-49.2) —
    /// the section to rewrite would be ambiguous — or when the new name is
    /// invalid or already used.</summary>
    public static void ReplaceBody(string path, string scriptName, string newBody, string? newName = null)
    {
        var text = ReadFile(path, out var encoding);
        File.WriteAllText(path, ReplaceBodyInText(text, scriptName, newBody, newName), encoding);
    }

    /// <summary>Appends a new `[scriptName]` section with
    /// <paramref name="body"/> at the end of the file, creating the file
    /// (and its directory) when it does not exist yet. The caller picks a
    /// name that is not already used.</summary>
    public static void AddScript(string path, string scriptName, string body)
    {
        var text = "";
        Encoding encoding = new UTF8Encoding(false);
        if (File.Exists(path))
            text = ReadFile(path, out encoding);
        else if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, AppendScriptToText(text, scriptName, body), encoding);
    }

    private static string ReadFile(string path, out Encoding encoding)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        encoding = reader.CurrentEncoding;
        return text;
    }

    internal static string AppendScriptToText(string text, string scriptName, string body)
    {
        var lineEnding = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        var content = text.TrimEnd('\r', '\n', ' ', '\t');
        var separator = content.Length == 0 ? "" : lineEnding + lineEnding;
        var bodyText = body.Replace("\r\n", "\n").Trim('\n').Replace("\n", lineEnding);
        return content + separator + "[" + scriptName + "]" + lineEnding + bodyText + lineEnding;
    }

    internal static string ReplaceBodyInText(string text, string scriptName, string newBody, string? newName = null)
    {
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var (headerIndex, sectionEnd) = FindSection(lines, scriptName);

        if (newName is not null && newName != scriptName)
        {
            if (!ScriptFileParser.IsValidName(newName))
                throw new InvalidOperationException($"'{newName}' is not a valid script name.");
            if (lines.Any(line => ScriptFileParser.TryGetSectionName(line, out var existing) && existing == newName))
                throw new InvalidOperationException($"A script named '{newName}' already exists.");

            lines[headerIndex] = $"[{newName}]";
        }

        var bodyStart = headerIndex + 1;
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

    /// <summary>Removes the section of <paramref name="scriptName"/> from
    /// the file (header, body and the blank lines that followed it). Throws
    /// <see cref="InvalidOperationException"/> when the name matches no
    /// section or several.</summary>
    public static void RemoveScript(string path, string scriptName)
    {
        var text = ReadFile(path, out var encoding);
        File.WriteAllText(path, RemoveScriptFromText(text, scriptName), encoding);
    }

    internal static string RemoveScriptFromText(string text, string scriptName)
    {
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var hadFinalNewline = lines[^1].Length == 0;
        var (headerIndex, sectionEnd) = FindSection(lines, scriptName);
        var wasLastSection = sectionEnd == lines.Count;

        lines.RemoveRange(headerIndex, sectionEnd - headerIndex);

        // Removing the last section leaves the previous section's separator
        // blank lines dangling at the end of the file: drop them, keeping
        // the file's final newline if it had one.
        if (wasLastSection)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);
            if (hadFinalNewline && lines.Count > 0)
                lines.Add("");
        }

        return string.Join(lineEnding, lines);
    }

    /// <summary>Moves the section of <paramref name="scriptName"/> up
    /// (<paramref name="offset"/> -1) or down (+1) among the sections of the
    /// file; a no-op at either end. Throws
    /// <see cref="InvalidOperationException"/> when the name matches no
    /// section or several.</summary>
    public static void MoveScript(string path, string scriptName, int offset)
    {
        var text = ReadFile(path, out var encoding);
        File.WriteAllText(path, MoveScriptInText(text, scriptName, offset), encoding);
    }

    internal static string MoveScriptInText(string text, string scriptName, int offset)
    {
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var (headerIndex, _) = FindSection(lines, scriptName);

        var headerIndexes = Enumerable.Range(0, lines.Count)
            .Where(i => ScriptFileParser.TryGetSectionName(lines[i], out _))
            .ToList();
        var current = headerIndexes.IndexOf(headerIndex);
        var target = current + offset;
        if (target < 0 || target >= headerIndexes.Count)
            return text;

        // Each section's content (header + body) is swapped, while the blank
        // lines closing a slot stay where they are: the spacing between
        // sections, and the file's final newline, do not move with the script.
        var contents = new List<List<string>>();
        var closings = new List<List<string>>();
        for (var i = 0; i < headerIndexes.Count; i++)
        {
            var start = headerIndexes[i];
            var end = i + 1 < headerIndexes.Count ? headerIndexes[i + 1] : lines.Count;
            var contentEnd = end;
            while (contentEnd > start + 1 && string.IsNullOrWhiteSpace(lines[contentEnd - 1]))
                contentEnd--;

            contents.Add(lines.GetRange(start, contentEnd - start));
            closings.Add(lines.GetRange(contentEnd, end - contentEnd));
        }

        (contents[current], contents[target]) = (contents[target], contents[current]);

        var result = lines.GetRange(0, headerIndexes[0]);
        for (var i = 0; i < contents.Count; i++)
        {
            result.AddRange(contents[i]);
            result.AddRange(closings[i]);
        }

        return string.Join(lineEnding, result);
    }

    /// <summary>Locates the section named <paramref name="scriptName"/>:
    /// the index of its header line and the index of the line closing it
    /// (the next header, or the end of the file).</summary>
    private static (int HeaderIndex, int SectionEnd) FindSection(List<string> lines, string scriptName)
    {
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

        return (matches[0], headerIndexes.FirstOrDefault(index => index > matches[0], lines.Count));
    }
}

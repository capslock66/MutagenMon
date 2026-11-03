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

    /// <summary>Applies several <see cref="ScriptEdit"/>s in order and
    /// writes the file once: when any edit fails (see
    /// <see cref="ReplaceBody"/>), nothing is written.</summary>
    public static void ReplaceBodies(string path, IEnumerable<ScriptEdit> edits)
    {
        var text = ReadFile(path, out var encoding);
        foreach (var edit in edits)
            text = ReplaceBodyInText(text, edit.ScriptName, edit.NewBody, edit.NewName);

        File.WriteAllText(path, text, encoding);
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
    /// (<paramref name="offset"/> -1) or down (+1) among the sections of its
    /// own group (those with the same `@group`, or those without one); a
    /// no-op at either end of the group. Throws
    /// <see cref="InvalidOperationException"/> when the name matches no
    /// section or several.</summary>
    public static void MoveScript(string path, string scriptName, int offset)
    {
        var text = ReadFile(path, out var encoding);
        File.WriteAllText(path, MoveScriptInText(text, scriptName, offset), encoding);
    }

    /// <summary>Moves every section of the group <paramref name="groupName"/>
    /// up (-1) or down (+1) past the neighbouring group; a no-op at either
    /// end. Groups are ordered by first appearance in the file. The sections
    /// of a group become contiguous and the sections without a group are
    /// placed after all groups, which is the order the Scripts tab shows.
    /// Throws <see cref="InvalidOperationException"/> when no section has
    /// that group.</summary>
    public static void MoveGroup(string path, string groupName, int offset)
    {
        var text = ReadFile(path, out var encoding);
        File.WriteAllText(path, MoveGroupInText(text, groupName, offset), encoding);
    }

    internal static string MoveScriptInText(string text, string scriptName, int offset)
    {
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var (headerIndex, _) = FindSection(lines, scriptName);
        var (prefix, sections) = SplitSections(lines);

        var current = sections.FindIndex(s_ => s_.HeaderIndex == headerIndex);
        var step = Math.Sign(offset);
        var target = current + step;
        // Sections of other groups in between keep their place: the script only changes order within its group.
        while (target >= 0 && target < sections.Count && sections[target].Group != sections[current].Group)
            target += step;
        if (step == 0 || target < 0 || target >= sections.Count)
            return text;

        var contents = sections.Select(s_ => s_.Content).ToList();
        var moved = contents[current];
        contents.RemoveAt(current);
        contents.Insert(target, moved);
        return JoinSections(prefix, sections, contents, lineEnding);
    }

    internal static string MoveGroupInText(string text, string groupName, int offset)
    {
        var lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var (prefix, sections) = SplitSections(lines);

        var groupOrder = sections
            .Select(s_ => s_.Group)
            .Where(g_ => g_.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var current = groupOrder.IndexOf(groupName);
        if (current < 0)
            throw new InvalidOperationException($"Group '{groupName}' was not found in the file.");

        var target = current + Math.Sign(offset);
        if (target == current || target < 0 || target >= groupOrder.Count)
            return text;

        (groupOrder[current], groupOrder[target]) = (groupOrder[target], groupOrder[current]);
        var contents = groupOrder
            .SelectMany(g_ => sections.Where(s_ => s_.Group == g_))
            .Concat(sections.Where(s_ => s_.Group.Length == 0))
            .Select(s_ => s_.Content)
            .ToList();
        return JoinSections(prefix, sections, contents, lineEnding);
    }

    /// <summary>One section of the file: its header line index, its group,
    /// its content (header + body) and the blank lines closing it.</summary>
    private sealed record Section(int HeaderIndex, string Group, List<string> Content, List<string> Closing);

    /// <summary>Splits the file into the lines before the first header and
    /// its sections.</summary>
    private static (List<string> Prefix, List<Section> Sections) SplitSections(List<string> lines)
    {
        var headerIndexes = Enumerable.Range(0, lines.Count)
            .Where(i => ScriptFileParser.TryGetSectionName(lines[i], out _))
            .ToList();
        if (headerIndexes.Count == 0)
            return (lines, new List<Section>());

        var sections = new List<Section>();
        for (var i = 0; i < headerIndexes.Count; i++)
        {
            var start = headerIndexes[i];
            var end = i + 1 < headerIndexes.Count ? headerIndexes[i + 1] : lines.Count;
            var contentEnd = end;
            while (contentEnd > start + 1 && string.IsNullOrWhiteSpace(lines[contentEnd - 1]))
                contentEnd--;

            var content = lines.GetRange(start, contentEnd - start);
            sections.Add(new Section(start, ScriptFileParser.ReadGroup(content), content, lines.GetRange(contentEnd, end - contentEnd)));
        }

        return (lines.GetRange(0, headerIndexes[0]), sections);
    }

    /// <summary>Writes <paramref name="contents"/> back slot by slot: the
    /// blank lines closing a slot stay where they are, so the spacing
    /// between sections and the file's final newline do not move with the
    /// sections.</summary>
    private static string JoinSections(List<string> prefix, List<Section> sections, List<List<string>> contents, string lineEnding)
    {
        var result = new List<string>(prefix);
        for (var i = 0; i < contents.Count; i++)
        {
            result.AddRange(contents[i]);
            result.AddRange(sections[i].Closing);
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

/// <summary>One pending change of a script: its new body and, optionally,
/// its new name (see <see cref="ScriptFileEditor.ReplaceBody"/>).</summary>
public sealed record ScriptEdit(string ScriptName, string NewBody, string? NewName = null);

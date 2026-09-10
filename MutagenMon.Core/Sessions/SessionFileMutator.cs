namespace MutagenMon.Core.Sessions;

/// <summary>
/// The three ways a session's line in `mutagen-create.bat` changes
/// (FR-17.4, FR-27.4): append a new one, replace an existing one in
/// place (same position — never moved to the end), or remove one
/// entirely. Everything else in the file (comments, blank lines, the
/// `mutagen sync terminate --all`/`mutagen sync list` housekeeping lines
/// some sessions files carry) is left untouched.
///
/// Pure line-array methods do the actual work so they're unit-testable
/// without touching disk; the `*File` methods are thin `File.ReadAllLines`
/// / `File.WriteAllLines` wrappers around them, mirroring
/// <see cref="SessionDefinitionLoader.ParseFile"/>'s split.
/// </summary>
public static class SessionFileMutator
{
    public static string[] AppendSession(IReadOnlyList<string> lines, string rawCreateCommand) =>
        [.. lines, rawCreateCommand];

    /// <summary>Throws if no session named <paramref name="name"/> is
    /// found — the caller (FR-27.5) is expected to already know it exists,
    /// having just loaded it into the Edit window.</summary>
    public static string[] ReplaceSession(IReadOnlyList<string> lines, string name, string newRawCreateCommand)
    {
        var index = FindLastSessionLineIndex(lines, name);
        if (index < 0)
            throw new InvalidOperationException($"No session named '{name}' found.");

        var result = lines.ToArray();
        result[index] = newRawCreateCommand;
        return result;
    }

    /// <summary>Throws if no session named <paramref name="name"/> is
    /// found (see <see cref="ReplaceSession"/>).</summary>
    public static string[] RemoveSession(IReadOnlyList<string> lines, string name)
    {
        var index = FindLastSessionLineIndex(lines, name);
        if (index < 0)
            throw new InvalidOperationException($"No session named '{name}' found.");

        var result = new List<string>(lines);
        result.RemoveAt(index);
        return result.ToArray();
    }

    public static void AppendSessionToFile(string path, string rawCreateCommand) =>
        File.WriteAllLines(path, AppendSession(File.ReadAllLines(path), rawCreateCommand));

    public static void ReplaceSessionInFile(string path, string name, string newRawCreateCommand) =>
        File.WriteAllLines(path, ReplaceSession(File.ReadAllLines(path), name, newRawCreateCommand));

    public static void RemoveSessionFromFile(string path, string name) =>
        File.WriteAllLines(path, RemoveSession(File.ReadAllLines(path), name));

    /// <summary>Last line wins on a duplicate name (FR-1.2) — same
    /// authoritative line <see cref="SessionDefinitionLoader"/> would
    /// report for that name, so Edit/Delete always act on the session the
    /// grid is actually showing.</summary>
    private static int FindLastSessionLineIndex(IReadOnlyList<string> lines, string name)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
            if (SessionDefinitionLoader.TryExtractName(lines[i], out _, out var extractedName) && extractedName == name)
                return i;
        return -1;
    }
}

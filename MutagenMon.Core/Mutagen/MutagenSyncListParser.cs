using System.Text.RegularExpressions;

namespace MutagenMon.Core.Mutagen;

/// <summary>
/// Pure port of the legacy session-status parsing logic (including the text
/// cleanup step performed before classification). Takes zero dependencies
/// on a real `mutagen` process — see NFR-11 — so the classification
/// pipeline it feeds is fully unit-testable.
///
/// Deliberate deviation from the legacy behavior: if the CLI output contains
/// a session name that is not in <paramref name="knownSessionNames"/> (a
/// stray/orphaned mutagen session not declared in the sessions file), the
/// legacy implementation raised an error there, which its caller silently
/// swallowed — meaning ALL status polling for EVERY session silently stops
/// every cycle as long as the stray session exists. This port instead just
/// carries the
/// extra entry along in the result (ignored by anything that only iterates
/// known session names); this is a robustness fix, not a documented
/// requirement change.
/// </summary>
public static partial class MutagenSyncListParser
{
    public static MutagenSyncListResult Parse(string rawOutput, IReadOnlyCollection<string> knownSessionNames)
    {
        var cleaned = Normalize(rawOutput);

        var builders = new Dictionary<string, Builder>();
        var conflicts = new Dictionary<string, List<ConflictRecord>>();
        foreach (var name in knownSessionNames) conflicts[name] = new List<ConflictRecord>();

        var currentName = "";
        var side = 0;
        var pendingAlphaName = "";
        var pendingAlphaState = "";

        foreach (var rawLine in cleaned.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("Name: ", StringComparison.Ordinal))
            {
                currentName = line[6..];
                var b = GetOrCreate(builders, currentName);
                b.IsDuplicate = b.Touched;
                b.Touched = true;
                b.HasConflicts = false;
                b.HasProblems = false;
                if (!conflicts.ContainsKey(currentName)) conflicts[currentName] = new List<ConflictRecord>();
                continue;
            }

            if (currentName.Length == 0) continue; // stray line before any "Name:" seen

            var current = GetOrCreate(builders, currentName);

            if (line.StartsWith("Identifier: ", StringComparison.Ordinal))
            {
                current.Id = line[12..];
            }
            else if (line.StartsWith("Status: ", StringComparison.Ordinal))
            {
                current.Status = line[8..];
            }
            else if (line.StartsWith("Alpha:", StringComparison.Ordinal))
            {
                side = 1;
            }
            else if (line.StartsWith("Beta:", StringComparison.Ordinal))
            {
                side = 2;
            }
            else if (line.StartsWith("URL: ", StringComparison.Ordinal))
            {
                var endpoint = BuildEndpoint(line[5..]);
                if (side == 1) current.Alpha = endpoint;
                else if (side == 2) current.Beta = endpoint;
            }
            else if (line.StartsWith("Conflicts:", StringComparison.Ordinal))
            {
                current.HasConflicts = true;
            }
            else if (line.StartsWith("Problems:", StringComparison.Ordinal))
            {
                current.HasProblems = true;
            }
            else if (line.StartsWith("(alpha) ", StringComparison.Ordinal))
            {
                var pos = FindMatchingOpenParen(line, line.Length - 1);
                if (pos is int p && p > 8)
                {
                    pendingAlphaName = line[8..(p - 1)];
                    pendingAlphaState = line[(p + 1)..];
                }
            }
            else if (line.StartsWith("(beta) ", StringComparison.Ordinal))
            {
                var pos = FindMatchingOpenParen(line, line.Length - 1);
                if (pos is int p && p > 7)
                {
                    var betaName = line[7..(p - 1)];
                    var betaState = line[(p + 1)..];
                    conflicts[currentName].Add(new ConflictRecord(pendingAlphaName, betaName, pendingAlphaState, betaState, AutoResolved: false));
                }
            }
        }

        var statuses = new Dictionary<string, ParsedSessionStatus?>();
        foreach (var name in knownSessionNames)
        {
            statuses[name] = builders.TryGetValue(name, out var b) && b.Touched ? b.ToParsedStatus(name) : null;
        }
        foreach (var (name, b) in builders)
        {
            if (!statuses.ContainsKey(name) && b.Touched)
            {
                statuses[name] = b.ToParsedStatus(name);
                conflicts.TryAdd(name, new List<ConflictRecord>());
            }
        }

        return new MutagenSyncListResult(
            cleaned,
            statuses,
            conflicts.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ConflictRecord>)kv.Value));
    }

    /// <summary>Strips daemon-startup noise and labels lines from the raw CLI
    /// output (a timestamp prefix is a display-only concern for the status view, FR-8 —
    /// out of scope here; RawLog is the cleaned body only).</summary>
    private static string Normalize(string raw)
    {
        var st = raw
            .Replace("Attempting to start Mutagen daemon...", "")
            .Replace("Started Mutagen daemon in background (terminate with \"mutagen daemon stop\")", "")
            .Replace("\r\n", "\n")
            .Replace("\n\t", "\n    ");
        st = LabelsLineRegex().Replace(st, "");
        return st.Trim().Trim('-');
    }

    [GeneratedRegex(@"Labels:.*?\n")]
    private static partial Regex LabelsLineRegex();

    private static Builder GetOrCreate(Dictionary<string, Builder> dict, string name)
    {
        if (!dict.TryGetValue(name, out var b))
        {
            b = new Builder();
            dict[name] = b;
        }
        return b;
    }

    /// <summary>Ports mutagen's own SCP-style-vs-Windows-path disambiguation:
    /// a single character before the first ':' is a Windows drive letter
    /// (e.g. "C:\..." or "C:/..."), not an SSH host. Anything else with a
    /// ':' is "[user@]host:path" — the path may or may not start with '/'
    /// (absolute vs. relative-to-home are both valid SSH specs).
    ///
    /// Bug fix: the previous heuristic required the literal substring ":/"
    /// (i.e. only matched an *absolute* remote path), so a relative-to-home
    /// endpoint like "tparent@pc-ub1:sources/appman" — a real, common mutagen
    /// URL, and exactly the one that surfaced this in production use — was
    /// misclassified as Local. That fed straight into ConflictFileClient's
    /// local-file-IO branch, which then tried to open "sources/appman/..."
    /// as a local path relative to the app's own working directory and threw
    /// an IOException.</summary>
    private static SessionEndpoint BuildEndpoint(string url)
    {
        var colonIndex = url.IndexOf(':', StringComparison.Ordinal);
        return colonIndex > 1
            ? new SessionEndpoint(url, TransportKind.Ssh, url[..colonIndex], url[(colonIndex + 1)..])
            : new SessionEndpoint(url, TransportKind.Local, null, null);
    }

    /// <summary>Finds, scanning backwards from just before <paramref name="closeIndex"/>
    /// (which the caller already knows holds the closing paren to match), the
    /// index of its matching '('.</summary>
    internal static int? FindMatchingOpenParen(string s, int closeIndex)
    {
        var depth = 0;
        for (var x = closeIndex - 1; x >= 0; x--)
        {
            if (s[x] == '(')
            {
                if (depth == 0) return x;
                depth--;
            }
            else if (s[x] == ')')
            {
                depth++;
            }
        }
        return null;
    }

    private sealed class Builder
    {
        public bool Touched;
        public bool IsDuplicate;
        public string? Id;
        public string Status = "";
        public bool HasProblems;
        public bool HasConflicts;
        public SessionEndpoint? Alpha;
        public SessionEndpoint? Beta;

        public ParsedSessionStatus ToParsedStatus(string name) =>
            new(name, Id, Status, IsDuplicate, HasProblems, HasConflicts, Alpha, Beta);
    }
}

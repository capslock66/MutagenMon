using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

/// <summary>
/// Turns the raw per-side states mutagen prints for a conflict
/// (<c>&lt;non-existent&gt; -&gt; File (hash)</c>, <c>File (h1) -&gt; File (h2)</c>,
/// <c>Directory -&gt; &lt;non-existent&gt;</c>, ...) into a sentence a user can read.
/// Each state is <c>&lt;what the last synchronized state was&gt; -&gt; &lt;what it is
/// now on that side&gt;</c>.
/// </summary>
public static class ConflictDescriber
{
    private enum ChangeKind
    {
        Created,
        Deleted,
        Modified,
        Replaced,
        Unknown,
    }

    private sealed record SideChange(ChangeKind Kind, string OldType, string NewType, string Raw);

    private const string NonExistent = "<non-existent>";

    /// <summary>E.g. "Created on both sides with different content", or
    /// "Alpha: modified file; Beta: deleted file". The beta path is appended
    /// only when it differs from the alpha one.</summary>
    public static string Describe(ConflictRecord conflict)
    {
        var alpha = Parse(conflict.AlphaState);
        var beta = Parse(conflict.BetaState);

        var text = (alpha.Kind, beta.Kind) switch
        {
            (ChangeKind.Created, ChangeKind.Created) when alpha.NewType == "file" && beta.NewType == "file"
                => "Created on both sides with different content",
            (ChangeKind.Created, ChangeKind.Created)
                => $"Created on both sides (alpha: {alpha.NewType}; beta: {beta.NewType})",
            (ChangeKind.Modified, ChangeKind.Modified) when alpha.NewType == beta.NewType
                => $"Modified on both sides ({alpha.NewType})",
            _ => $"Alpha: {Phrase(alpha)}; Beta: {Phrase(beta)}",
        };

        return conflict.AlphaName == conflict.BetaName ? text : $"{text} (beta path: {conflict.BetaName})";
    }

    /// <summary>The raw mutagen states, for reference next to the readable
    /// description.</summary>
    public static string Details(ConflictRecord conflict) =>
        $"alpha: {conflict.AlphaState}\nbeta: {conflict.BetaState}";

    private static string Phrase(SideChange change) => change.Kind switch
    {
        ChangeKind.Created => $"created {change.NewType}",
        ChangeKind.Deleted => $"deleted {change.OldType}",
        ChangeKind.Modified => $"modified {change.NewType}",
        ChangeKind.Replaced => $"{change.OldType} replaced by {change.NewType}",
        _ => change.Raw,
    };

    private static SideChange Parse(string state)
    {
        var arrow = IndexOfTopLevelArrow(state);
        if (arrow < 0)
            return new SideChange(ChangeKind.Unknown, "", "", state);

        var oldState = state[..arrow].Trim();
        var newState = state[(arrow + 4)..].Trim();
        var oldExists = !oldState.StartsWith(NonExistent, StringComparison.Ordinal);
        var newExists = !newState.StartsWith(NonExistent, StringComparison.Ordinal);
        var oldType = TypeOf(oldState);
        var newType = TypeOf(newState);

        var kind = (oldExists, newExists) switch
        {
            (false, true) => ChangeKind.Created,
            (true, false) => ChangeKind.Deleted,
            (true, true) => oldType == newType ? ChangeKind.Modified : ChangeKind.Replaced,
            _ => ChangeKind.Unknown,
        };
        return new SideChange(kind, oldType, newType, state);
    }

    /// <summary>The " -&gt; " separating old and new state, ignoring any inside
    /// parentheses (a symbolic link's target can contain one).</summary>
    private static int IndexOfTopLevelArrow(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(')
                depth++;
            else if (s[i] == ')')
                depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, " -> ", 0, 4) == 0)
                return i;
        }

        return -1;
    }

    private static string TypeOf(string state)
    {
        if (state.StartsWith("File", StringComparison.Ordinal))
            return "file";
        if (state.StartsWith("Directory", StringComparison.Ordinal))
            return "directory";
        if (state.StartsWith("Symbolic link", StringComparison.Ordinal))
            return "symbolic link";
        return state;
    }
}

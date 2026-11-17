using System.Text.RegularExpressions;

namespace MutagenMon.Core.Scripts;

/// <summary>The known macro types (FR-55.1). A type name that is not listed
/// is <see cref="Unknown"/>, an error at expansion time, never ignored.</summary>
public enum ScriptMacroType
{
    Unknown,
    Env,
    Session,
    Reg,
    Cred,
    Secret,
    Prompt,
}

/// <summary>One value the user is asked to type before a run (FR-55.11,
/// FR-55.12): <paramref name="IsSecret"/> is <c>true</c> for a <c>secret</c> macro
/// (hidden input, masked output) and <c>false</c> for a <c>prompt</c> macro
/// (visible input, output not masked).
/// <paramref name="Default"/> is the initial value of the input (FR-55.12), or <c>null</c> when
/// no macro of the label has a non-empty <c>|default=</c> modifier.</summary>
public sealed record ScriptValueRequest(string Label, bool IsSecret, string? Default = null);

/// <summary>One macro token found in a script text (FR-55.1): <c>{{type:name}}</c>,
/// optionally <c>{{type:name|quote}}</c> (<paramref name="IsQuoted"/>) and
/// <c>{{prompt:name|default=value}}</c> (<paramref name="DefaultText"/>: <c>null</c> when the
/// modifier is absent, empty when it is written without a value).
/// <paramref name="Start"/> and <paramref name="Length"/> are the span of
/// the whole token in the text, so a caller can replace it (or, later,
/// colour it).</summary>
public sealed record ScriptMacro(int Start, int Length, string TypeName, string Name, bool IsQuoted, string? DefaultText = null)
{
    // Matches one macro token, e.g. "{{env:DB_PWD}}", "{{secret:Database password}}", "{{env:DB_PWD|quote}}" or "{{prompt:Command|default=/config|quote}}".
    // Groups: 1=type (a letter, then letters, digits, "_" or "-": a mistyped type such as "env2" is reported as unknown, not ignored), 2=name (any text without a line break and without "{{", up to the first place where the optional modifiers and "}}" follow,
    // possibly empty), named "quote" = the "|quote" modifier, named "default" = the value of the "|default=" modifier (possibly empty:
    // a text without "|", without a line break and without "}}"). The two modifiers are optional, in either order and each at most once;
    // a group does not match when its modifier is absent. Access via match.Groups[n] or match.Groups["quote"] / match.Groups["default"].
    // The name is lazy and stops at the first place where the modifiers (if any) and "}}" follow, so a name may contain "|" or "=".
    // "{{" is excluded from the name so that an unclosed token cannot swallow the next one:
    // in `{{env:X" ; echo {{env:Y}}` only {{env:Y}} is a macro.
    // Text that merely contains "{{" (no "type:" after it) or bash "${VAR}" / "$(cmd)" does not match.
    private const string DefaultValuePattern = @"(?:(?!\{\{)[^|\r\n}]|\}(?!\}))*";
    private static readonly Regex MacroRegex = new(
        @"\{\{([A-Za-z][A-Za-z0-9_-]*):((?:(?!\{\{)[^\r\n])*?)"
        + @"(?:\|(?<quote>quote)(?:\|default=(?<default>" + DefaultValuePattern + @"))?"
        + @"|\|default=(?<default>" + DefaultValuePattern + @")(?:\|(?<quote>quote))?)?\}\}",
        RegexOptions.CultureInvariant);

    /// <summary>The initial value of the input for a <c>prompt</c> macro, or <c>null</c> when
    /// there is none (modifier absent or empty, FR-55.12).</summary>
    public string? Default => string.IsNullOrEmpty(DefaultText) ? null : DefaultText;

    public ScriptMacroType Type => TypeName switch
    {
        "env" => ScriptMacroType.Env,
        "session" => ScriptMacroType.Session,
        "reg" => ScriptMacroType.Reg,
        "cred" => ScriptMacroType.Cred,
        "secret" => ScriptMacroType.Secret,
        "prompt" => ScriptMacroType.Prompt,
        _ => ScriptMacroType.Unknown,
    };

    /// <summary>The macro in a normalised source form (modifiers in the order <c>default</c>, <c>quote</c>), for
    /// error messages. It never holds a resolved value. The text of a <c>default</c> modifier is shown for a
    /// <c>prompt</c> macro only: on any other type it is replaced by <c>…</c>, because an author may have typed a secret there
    /// (FR-53.3) and the messages are logged. For the same reason the name is <see cref="SafeName"/>.</summary>
    public string Display => "{{" + TypeName + ":" + SafeName
        + (DefaultText is null ? "" : "|default=" + (Type == ScriptMacroType.Prompt ? DefaultText : "…")) + (IsQuoted ? "|quote" : "") + "}}";

    /// <summary>The name without the text of a <c>|default=</c> modifier that the token left in it: a default containing
    /// <c>|</c> (<c>{{env:X|default=a|b}}</c>) or a repeated <c>|default=</c> is not isolated by the pattern and ends
    /// up in the name. Every message that names a macro uses this name, never <see cref="Name"/>, so that the text of a
    /// default written by mistake on a <c>secret</c>, <c>env</c>… macro (FR-53.3) cannot reach the log.</summary>
    public string SafeName
    {
        get
        {
            var cut = Name.IndexOf("|default=", StringComparison.Ordinal);
            return cut < 0 ? Name : Name[..cut];
        }
    }

    /// <summary>The label to report when the token is malformed (FR-55.1, FR-55.12), otherwise <c>null</c>. A token is
    /// malformed when a <c>|default=</c> is left in the name of any macro (a default containing <c>|</c>, or a repeated
    /// <c>default</c>), or when the name of a <c>prompt</c> macro still holds a <c>|quote</c> modifier (repeated
    /// <c>quote</c>); the modifier would silently become part of the label. A repeated <c>|quote</c> on another type
    /// stays part of its name, as before the modifiers existed.</summary>
    public string? MalformedModifierLabel
    {
        get
        {
            if (Name.Contains("|default=", StringComparison.Ordinal))
                return SafeName;
            if (Type != ScriptMacroType.Prompt)
                return null;

            var quoteMatch = RepeatedQuoteRegex.Match(Name);
            return quoteMatch.Success ? Name[..quoteMatch.Index] : null;
        }
    }

    /// <summary>A rejected macro is reported as an error and never resolved nor asked for: its token is malformed,
    /// it carries a default and is not a <c>prompt</c> macro (an unknown type has its own error), or the default of a
    /// <c>prompt</c> macro contains a NUL character (FR-55.5, FR-55.12).</summary>
    public bool IsRejected =>
        MalformedModifierLabel is not null
        || DefaultText is not null && Type is not (ScriptMacroType.Prompt or ScriptMacroType.Unknown)
        || Type == ScriptMacroType.Prompt && DefaultText is not null && DefaultText.Contains('\0');

    // Matches a "|quote" modifier left in a name: "|quote" followed by "|" or by the end of the name ("|quoted" does not match).
    private static readonly Regex RepeatedQuoteRegex = new(@"\|quote(?=\||$)", RegexOptions.CultureInvariant);

    /// <summary>Every macro token of <paramref name="text"/> that is not in a
    /// comment, in order of appearance. A token never spans a line break.
    /// Everything to the right of a bash comment marker is ignored (FR-55.1).</summary>
    public static IReadOnlyList<ScriptMacro> Find(string text)
    {
        var macros = MacroRegex.Matches(text)
            .Select(match_ => new ScriptMacro(
                match_.Index, match_.Length, match_.Groups[1].Value, match_.Groups[2].Value, match_.Groups["quote"].Success,
                match_.Groups["default"].Success ? match_.Groups["default"].Value : null))
            .ToArray();
        if (macros.Length == 0 || !text.Contains('#'))
            return macros;

        var commentStarts = FindCommentStarts(text, macros);
        if (commentStarts.Count == 0)
            return macros;

        return macros.Where(macro_ => !IsInComment(macro_, commentStarts, text)).ToArray();
    }

    // A macro is in a comment when a comment starts earlier on its own line.
    private static bool IsInComment(ScriptMacro macro, List<int> commentStarts, string text)
    {
        var lineStart = text.LastIndexOf('\n', macro.Start) + 1;
        return commentStarts.Any(commentStart_ => commentStart_ >= lineStart && commentStart_ < macro.Start);
    }

    // Returns the index of the "#" that starts a comment, for each line that has one. Bash rules, applied line by line:
    // a "#" starts a comment only at the beginning of a word (start of the line, or right after a space, a tab, ";", "&", "|", "(", ")", "<" or ">"),
    // never inside single or double quotes or after a backslash, and never in "$#" or "${#var}".
    // Quotes are tracked within one line only: a "#" at the start of a line inside a multi-line quoted string or a here-document
    // is seen as a comment. The text of a macro is skipped, so a quote or a "#" in a macro name (for example
    // "{{secret:Bob's password}}" or "{{secret:Item #2}}") neither opens a quote nor starts a comment.
    private static List<int> FindCommentStarts(string text, ScriptMacro[] macros)
    {
        var commentStarts = new List<int>();
        var macroIndex = 0;
        var isInSingleQuote = false;
        var isInDoubleQuote = false;
        var isCommentOnThisLine = false;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current == '\n')
            {
                isInSingleQuote = false;
                isInDoubleQuote = false;
                isCommentOnThisLine = false;
                continue;
            }

            if (isCommentOnThisLine)
                continue;

            while (macroIndex < macros.Length && macros[macroIndex].Start + macros[macroIndex].Length <= i)
                macroIndex++;
            if (macroIndex < macros.Length && macros[macroIndex].Start <= i)
            {
                i = macros[macroIndex].Start + macros[macroIndex].Length - 1;
                continue;
            }

            if (isInSingleQuote)
                isInSingleQuote = current != '\'';
            else if (current == '\\')
                i++;
            else if (isInDoubleQuote)
                isInDoubleQuote = current != '"';
            else if (current == '\'')
                isInSingleQuote = true;
            else if (current == '"')
                isInDoubleQuote = true;
            else if (current == '#' && IsStartOfWord(text, i))
            {
                commentStarts.Add(i);
                isCommentOnThisLine = true;
            }
        }

        return commentStarts;
    }

    // Only the blanks and metacharacters of bash: a no-break space or another Unicode space (pasted from a document) is part of a word.
    private static bool IsStartOfWord(string text, int index) =>
        index == 0 || text[index - 1] is ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or '(' or ')' or '<' or '>';

    /// <summary>The values to ask for, one per distinct label of the <c>secret</c>
    /// and <c>prompt</c> macros, in order of first appearance — one row per
    /// label in the dialog (FR-55.11, FR-55.12). Labels are case-sensitive; an
    /// empty label is not listed (it is an error at expansion time). A label used by both
    /// types is listed once, with the type of its first appearance. The caller must run
    /// <c>ScriptMacroExpander.Expand</c> with placeholder values before showing the dialog:
    /// it reports such a label as an error (FR-55.12), so that no dialog is shown.</summary>
    public static IReadOnlyList<ScriptValueRequest> ValueRequests(IEnumerable<ScriptMacro> macros) =>
        macros
            .Where(macro_ => macro_.Type is ScriptMacroType.Secret or ScriptMacroType.Prompt && macro_.Name.Length > 0 && !macro_.IsRejected)
            .GroupBy(macro_ => macro_.Name, StringComparer.Ordinal)
            .Select(group_ => new ScriptValueRequest(
                group_.Key,
                group_.First().Type == ScriptMacroType.Secret,
                group_.First().Type == ScriptMacroType.Secret
                    ? null
                    : group_.Where(macro_ => macro_.Type == ScriptMacroType.Prompt).Select(macro_ => macro_.Default).FirstOrDefault(default_ => default_ is not null)))
            .ToArray();
}

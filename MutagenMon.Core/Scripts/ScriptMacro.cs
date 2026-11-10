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
}

/// <summary>One macro token found in a script text (FR-55.1): <c>{{type:name}}</c>,
/// optionally <c>{{type:name|quote}}</c> (<paramref name="IsQuoted"/>).
/// <paramref name="Start"/> and <paramref name="Length"/> are the span of
/// the whole token in the text, so a caller can replace it (or, later,
/// colour it).</summary>
public sealed record ScriptMacro(int Start, int Length, string TypeName, string Name, bool IsQuoted)
{
    // Matches one macro token, e.g. "{{env:DB_PWD}}", "{{secret:Database password}}" or "{{env:DB_PWD|quote}}".
    // Groups: 1=type (a letter, then letters, digits, "_" or "-": a mistyped type such as "env2" is reported as unknown, not ignored), 2=name (any text without a line break and without "{{", up to the first "}}",
    // possibly empty), 3=the "|quote" modifier (the group does not match when there is no modifier).
    // Access via match.Groups[n]. "{{" is excluded from the name so that an unclosed token cannot swallow the next one:
    // in `{{env:X" ; echo {{env:Y}}` only {{env:Y}} is a macro.
    // Text that merely contains "{{" (no "type:" after it) or bash "${VAR}" / "$(cmd)" does not match.
    private static readonly Regex MacroRegex = new(@"\{\{([A-Za-z][A-Za-z0-9_-]*):((?:(?!\{\{)[^\r\n])*?)(\|quote)?\}\}", RegexOptions.CultureInvariant);

    public ScriptMacroType Type => TypeName switch
    {
        "env" => ScriptMacroType.Env,
        "session" => ScriptMacroType.Session,
        "reg" => ScriptMacroType.Reg,
        "cred" => ScriptMacroType.Cred,
        "secret" => ScriptMacroType.Secret,
        _ => ScriptMacroType.Unknown,
    };

    /// <summary>The macro in its source form, for error messages. It holds
    /// only the type, the name and the modifier, never a resolved value.</summary>
    public string Display => "{{" + TypeName + ":" + Name + (IsQuoted ? "|quote" : "") + "}}";

    /// <summary>Every macro token of <paramref name="text"/> that is not in a
    /// comment, in order of appearance. A token never spans a line break.
    /// Everything to the right of a bash comment marker is ignored (FR-55.1).</summary>
    public static IReadOnlyList<ScriptMacro> Find(string text)
    {
        var macros = MacroRegex.Matches(text)
            .Select(match_ => new ScriptMacro(
                match_.Index, match_.Length, match_.Groups[1].Value, match_.Groups[2].Value, match_.Groups[3].Success))
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

    /// <summary>The labels of the <c>secret</c> macros, each listed once,
    /// in order of first appearance — one row per label in the dialog that
    /// asks for the values (FR-55.11). Labels are case-sensitive; an empty
    /// label is not listed (it is an error at expansion time).</summary>
    public static IReadOnlyList<string> SecretLabels(IEnumerable<ScriptMacro> macros) =>
        macros
            .Where(macro_ => macro_.Type == ScriptMacroType.Secret && macro_.Name.Length > 0)
            .Select(macro_ => macro_.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

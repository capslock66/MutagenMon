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

    /// <summary>Every macro token of <paramref name="text"/>, in order of
    /// appearance. A token never spans a line break.</summary>
    public static IReadOnlyList<ScriptMacro> Find(string text) =>
        MacroRegex.Matches(text)
            .Select(match_ => new ScriptMacro(
                match_.Index, match_.Length, match_.Groups[1].Value, match_.Groups[2].Value, match_.Groups[3].Success))
            .ToArray();

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

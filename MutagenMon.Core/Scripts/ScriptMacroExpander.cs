using System.Globalization;
using System.Reflection;
using System.Text;
using MutagenMon.Core.Ssh;
using MutagenMon.Core.Status;

namespace MutagenMon.Core.Scripts;

/// <summary>Where the macro values come from (FR-55). Core reads nothing
/// itself: the application passes the Windows readers and the typed secrets
/// as delegates, so the expansion is testable without a registry, a
/// Credential Manager or a dialog (NFR-11). A delegate returns
/// <c>null</c> when the value does not exist or cannot be read: it must not
/// throw for a missing or unreadable value, which is reported as an
/// unresolved macro (FR-55.6), not as an exception.</summary>
/// <param name="Environment">Environment variable name → value (FR-55.2).</param>
/// <param name="Registry">Normalised key (<c>HKCU\...</c> or <c>HKLM\...</c>) and value name → value as text (FR-55.9).</param>
/// <param name="Credential">Credential Manager target → password (FR-55.10).</param>
/// <param name="Secret">Label → value typed by the user (FR-55.11).</param>
/// <param name="SelectedSession">The session row selected in the Sync grid, or <c>null</c> (FR-55.3).</param>
public sealed record ScriptMacroSources(
    Func<string, string?> Environment,
    Func<string, string, string?> Registry,
    Func<string, string?> Credential,
    Func<string, string?> Secret,
    SessionSummaryRow? SelectedSession);

/// <summary>The result of <see cref="ScriptMacroExpander.Expand"/>.
/// <paramref name="Text"/> is the expanded script when <see cref="IsSuccess"/>
/// and an empty string otherwise (a failed expansion must never be run).
/// <paramref name="Errors"/> names every unresolved macro, never a value.
/// <paramref name="SecretsToMask"/> holds the values that came from an
/// <c>env</c>, <c>reg</c>, <c>cred</c> or <c>secret</c> macro (FR-55.8).</summary>
public sealed record ScriptMacroExpansion(
    string Text, IReadOnlyList<string> Errors, IReadOnlyList<string> SecretsToMask)
{
    public bool IsSuccess => Errors.Count == 0;

    /// <summary>The synthesized record text would print <see cref="Text"/> and
    /// <see cref="SecretsToMask"/>: the expanded script holds secret values,
    /// which must never reach a log or an exception message through
    /// <c>ToString()</c> (FR-54.3, FR-55.8).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"IsSuccess = {IsSuccess}, Errors = {Errors.Count}");
        return true;
    }
}

/// <summary>Replaces the macros of a script text by their values, just
/// before the run (FR-55.7). Pure: no I/O, no logging; the expanded text is
/// never logged or displayed (FR-54.3).</summary>
public static class ScriptMacroExpander
{
    /// <summary>Expands every macro of <paramref name="text"/>. Insertion is
    /// raw unless the macro has the <c>|quote</c> modifier (FR-55.4). A
    /// value with a line break or a NUL is refused (FR-55.5). Every
    /// unresolved macro is reported, none is substituted silently (FR-55.6).</summary>
    public static ScriptMacroExpansion Expand(string text, ScriptMacroSources sources)
    {
        var macros = ScriptMacro.Find(text);
        if (macros.Count == 0)
            return new ScriptMacroExpansion(text, [], []);

        var errors = new List<string>();
        var secretsToMask = new List<string>();
        var builder = new StringBuilder(text.Length);
        var position = 0;
        // One resolution per distinct macro: a registry value or a credential is read once per run,
        // every occurrence gets the same value, and a missing value is reported once.
        var resolutions = new Dictionary<(string TypeName, string Name), Resolution>();
        foreach (var macro in macros)
        {
            builder.Append(text, position, macro.Start - position);
            position = macro.Start + macro.Length;

            if (!resolutions.TryGetValue((macro.TypeName, macro.Name), out var resolution))
            {
                resolution = ResolveChecked(macro, sources);
                resolutions[(macro.TypeName, macro.Name)] = resolution;
            }

            if (resolution is not { Value: { } value })
            {
                // The first occurrence of a failing macro reports it; the later ones add nothing.
                var error = resolution.Error ?? $"{macro.Display}: the macro could not be resolved";
                if (!errors.Contains(error, StringComparer.Ordinal))
                    errors.Add(error);
                continue;
            }

            if (resolution.IsMasked)
            {
                AddOnce(secretsToMask, value);
                // `set -x` traces a value containing a single quote with each quote escaped as '\''
                // (a'b is traced as 'a'\''b', and -pa'b as '-pa'\''b'), whether or not the macro has |quote:
                // mask the escaped form too, without outer quotes so that it is found inside a longer word.
                // Without a single quote the raw value is a substring of the trace and is masked as is.
                if (value.Contains('\''))
                    AddOnce(secretsToMask, ShellQuoting.EscapeSingleQuotes(value));
            }

            builder.Append(macro.IsQuoted ? ShellQuoting.Quote(value) : value);
        }

        builder.Append(text, position, text.Length - position);
        return errors.Count > 0
            ? new ScriptMacroExpansion(string.Empty, errors, [])
            : new ScriptMacroExpansion(builder.ToString(), errors, secretsToMask);
    }

    private static void AddOnce(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
            values.Add(value);
    }

    private sealed record Resolution(string? Value, string? Error, bool IsMasked);

    private static Resolution Resolved(string value, bool isMasked) => new(value, null, isMasked);

    private static Resolution Failed(ScriptMacro macro, string reason) => new(null, $"{macro.Display}: {reason}", false);

    /// <summary>Resolves a macro and refuses a value with a line break or a
    /// NUL (FR-55.5): a line break would inject a new command line into the
    /// script. Done here so that the outcome is cached with the resolution.</summary>
    private static Resolution ResolveChecked(ScriptMacro macro, ScriptMacroSources sources)
    {
        var resolution = Resolve(macro, sources);
        if (resolution.Value is { } value && (value.Contains('\r') || value.Contains('\n') || value.Contains('\0')))
            return Failed(macro, "the value contains a line break or a NUL character");
        return resolution;
    }

    private static Resolution Resolve(ScriptMacro macro, ScriptMacroSources sources)
    {
        if (macro.Type is ScriptMacroType.Env or ScriptMacroType.Session or ScriptMacroType.Cred or ScriptMacroType.Secret
            && macro.Name.Length == 0)
            return Failed(macro, "the name is empty");

        return macro.Type switch
        {
            ScriptMacroType.Env => FromLookup(
                macro, sources.Environment(macro.Name),
                $"environment variable '{macro.Name}' is not defined or is empty"),
            ScriptMacroType.Session => ResolveSession(macro, sources.SelectedSession),
            ScriptMacroType.Reg => ResolveRegistry(macro, sources.Registry),
            ScriptMacroType.Cred => FromLookup(
                macro, sources.Credential(macro.Name),
                $"credential '{macro.Name}' was not found or has an empty password"),
            ScriptMacroType.Secret => FromLookup(
                macro, sources.Secret(macro.Name),
                $"no value was entered for the secret '{macro.Name}'"),
            _ => Failed(macro, $"unknown macro type '{macro.TypeName}' (known types: env, session, reg, cred, secret)"),
        };
    }

    /// <summary>A value read from an external source: missing or empty is an
    /// error, and a present value is masked in the output (FR-55.8).</summary>
    private static Resolution FromLookup(ScriptMacro macro, string? value, string errorWhenMissing) =>
        string.IsNullOrEmpty(value) ? Failed(macro, errorWhenMissing) : Resolved(value, isMasked: true);

    private static Resolution ResolveSession(ScriptMacro macro, SessionSummaryRow? session)
    {
        if (session is null)
            return Failed(macro, "no session is selected in the Sync grid");

        // Every public property of the session row is supported; the name is matched
        // ignoring case so that {{session:name}} reads the Name property (FR-55.3).
        var property = typeof(SessionSummaryRow).GetProperty(
            macro.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || property.GetIndexParameters().Length > 0)
            return Failed(macro, $"unknown session property '{macro.Name}'");

        // Session values are context, not secrets: an empty value is allowed and the value is not masked.
        return Resolved(FormatSessionValue(property.GetValue(session)), isMasked: false);
    }

    private static string FormatSessionValue(object? value) => value switch
    {
        null => "",
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static Resolution ResolveRegistry(ScriptMacro macro, Func<string, string, string?> registry)
    {
        // The LAST ':' separates the key from the value name (FR-55.9): a value name cannot contain ':'.
        var separator = macro.Name.LastIndexOf(':');
        if (separator <= 0 || separator == macro.Name.Length - 1)
            return Failed(macro, "expected the form {{reg:<key>:<value name>}} with a non-empty key and a non-empty value name");

        var key = macro.Name[..separator];
        var valueName = macro.Name[(separator + 1)..];

        var firstBackslash = key.IndexOf('\\');
        var hiveText = firstBackslash < 0 ? key : key[..firstBackslash];
        var subKey = firstBackslash < 0 ? "" : key[(firstBackslash + 1)..];

        var hive = NormaliseHive(hiveText);
        if (hive is null)
            return Failed(macro, $"unknown registry hive '{hiveText}' (only HKCU and HKLM are supported)");
        if (subKey.Length == 0)
            return Failed(macro, "the registry key path after the hive is empty");

        return FromLookup(
            macro, registry(hive + "\\" + subKey, valueName),
            $"registry value '{valueName}' of key '{hive}\\{subKey}' was not found, is empty or has an unsupported type");
    }

    /// <summary>Short hive name for the supported hives, accepting the long
    /// names, or <c>null</c> for any other hive.</summary>
    private static string? NormaliseHive(string hive)
    {
        if (hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            return "HKCU";
        if (hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            return "HKLM";
        return null;
    }
}

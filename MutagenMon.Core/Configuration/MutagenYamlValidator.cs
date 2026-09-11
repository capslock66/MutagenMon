using YamlDotNet.RepresentationModel;

namespace MutagenMon.Core.Configuration;

/// <summary>
/// Purely syntactic YAML validation for the mutagen config editor (FR-31,
/// requirements/08-mutagen-config-editor-requirements.md) — no schema-level
/// validation against mutagen's own expected keys/values is attempted;
/// mutagen itself is the authority on which keys are actually meaningful,
/// the same reasoning already used elsewhere in this app for values only the
/// external tool can validate (FR-18.3, FR-21.4 in
/// 07-session-management-requirements.md).
/// </summary>
public static class MutagenYamlValidator
{
    /// <summary>Empty on valid YAML; otherwise the parser's error message,
    /// including line/column when available. A single document can only
    /// surface one syntax error at a time (parsing stops there), so this is
    /// at most a one-element list — kept as a list so a caller iterating it
    /// doesn't need to change if that ever stops being true.
    /// Catches any exception, not just YamlDotNet's own
    /// <c>YamlException</c>: some malformed inputs (e.g. an unterminated
    /// flow sequence) trip an internal scanner assumption and surface as a
    /// plain <see cref="InvalidOperationException"/> instead — this is
    /// user-typed free text being actively edited, so any parse failure
    /// must turn into an FR-31 error message, never an unhandled
    /// exception.</summary>
    public static IReadOnlyList<string> Validate(string yamlText)
    {
        try
        {
            new YamlStream().Load(new StringReader(yamlText));
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            return new[] { ex.Message };
        }
    }
}

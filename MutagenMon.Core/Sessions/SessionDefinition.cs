namespace MutagenMon.Core.Sessions;

/// <summary>One monitored mutagen session, as declared in the sessions file (FR-1.1).
/// <paramref name="HaltedAutoFix"/> is the session's own `@halted-auto-fix`
/// override (FR-64), null when it inherits the global flag.</summary>
public sealed record SessionDefinition(string Name, string RawCreateCommand, bool? HaltedAutoFix = null);

/// <summary>
/// DuplicateNames lets the caller decide how to surface FR-1.2's warning
/// (log, dialog, etc.) — Core stays UI-agnostic. HaltedAutoFixDirectives holds
/// the per-session `@halted-auto-fix` overrides (FR-64): a session with no
/// entry inherits the global flag. DirectiveWarnings lists the directive lines
/// that were ignored (invalid value, unknown or disabled session), for the
/// caller to log.
/// </summary>
public sealed record SessionDefinitionLoadResult(
    IReadOnlyList<SessionDefinition> Sessions,
    IReadOnlyList<string> DuplicateNames,
    IReadOnlyDictionary<string, bool> HaltedAutoFixDirectives,
    IReadOnlyList<string> DirectiveWarnings);

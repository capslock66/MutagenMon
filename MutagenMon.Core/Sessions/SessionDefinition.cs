namespace MutagenMon.Core.Sessions;

/// <summary>One monitored mutagen session, as declared in the sessions file (FR-1.1).</summary>
public sealed record SessionDefinition(string Name, string RawCreateCommand);

/// <summary>
/// DuplicateNames lets the caller decide how to surface FR-1.2's warning
/// (log, dialog, etc.) — Core stays UI-agnostic.
/// </summary>
public sealed record SessionDefinitionLoadResult(
    IReadOnlyList<SessionDefinition> Sessions,
    IReadOnlyList<string> DuplicateNames);

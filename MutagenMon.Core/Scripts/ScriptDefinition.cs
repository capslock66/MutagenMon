namespace MutagenMon.Core.Scripts;

/// <summary>
/// One script parsed out of the scripts file (FR-48). <see cref="Body"/> is
/// the text sent verbatim to bash (directive comments included, `\n` line
/// endings, no `set -e` prefix — that is the runner's job, FR-51.2).
/// A non-null <see cref="ValidationError"/> means the script is listed but
/// cannot be run (FR-49.2). <see cref="Group"/> is the `@group` directive
/// (FR-48.5), empty when the script belongs to no group.
/// <see cref="IsSessionScript"/> is the value of the `@scriptSession` flag
/// (FR-48.5, FR-56): true when the script carries it, so the Sync tab offers
/// it for the selected session.
/// </summary>
public sealed record ScriptDefinition(
    string Name,
    string? Host,
    string Description,
    bool StopOnError,
    bool Confirm,
    string Body,
    string? ValidationError,
    string Group = "",
    bool IsSessionScript = false)
{
    public bool IsValid => ValidationError is null;
}

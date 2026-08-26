namespace MutagenMon.Core.Mutagen;

/// <summary>
/// Thin boundary around the `mutagen` CLI. Deliberately not unit-tested
/// (it just spawns a process) — <see cref="MutagenSyncListParser"/> is the
/// tested boundary; tests drive the pipeline through a fake implementation
/// of this interface (NFR-11).
/// </summary>
public interface IMutagenCliClient
{
    Task<string> GetSyncListRawAsync(CancellationToken cancellationToken);

    /// <summary>Ports mutagenmonlib/remote/mutagen.py: stop_session() —
    /// `mutagen sync terminate &lt;name&gt;`.</summary>
    Task TerminateSessionAsync(string sessionName, CancellationToken cancellationToken);

    /// <summary>Ports mutagenmonlib/remote/mutagen.py: start_session() (FR-13.5) —
    /// re-runs the session's original `mutagen sync create ...` command line
    /// (<see cref="Sessions.SessionDefinition.RawCreateCommand"/>), with its
    /// first token replaced by the configured mutagen path.</summary>
    Task CreateSessionAsync(string rawCreateCommand, CancellationToken cancellationToken);
}

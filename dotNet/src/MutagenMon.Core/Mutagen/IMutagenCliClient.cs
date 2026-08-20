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
}

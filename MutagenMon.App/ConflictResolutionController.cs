using System.Windows;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>
/// Implements the batch resolution workflow (FR-9) — the batch
/// loop that presents each unresolved conflict in turn via
/// <see cref="ConflictResolutionWindow"/>, applies the chosen resolution
/// through <see cref="ConflictResolutionService"/>, and aborts the whole
/// batch the moment the user cancels (FR-9.4), plus its own batch-assembly
/// and too-many-conflicts guard (FR-9.5) — pure, so it stays easy to reason
/// about despite living next to the UI loop that drives it.
///
/// Deliberate deviation from the legacy behavior: the legacy passes
/// <c>len(conflicts)</c> (the number of session keys) as the "total" shown
/// in "N of total", not the actual number of unresolved conflicts, which
/// undercounts whenever a session has more than one conflict. FR-9.1 asks
/// for "N of total [conflicts]", so <see cref="Flatten"/> counts real
/// unresolved conflicts instead.
/// </summary>
public sealed class ConflictResolutionController
{
    /// <summary>FR-9.5: refuse to start the batch workflow above this many
    /// pending (non-autoresolved) conflicts.</summary>
    private const int MaxBatchSize = 100;

    private readonly Window _owner;
    private readonly SessionStateStore _stateStore;
    private readonly IReadOnlyList<string> _sessionNames;
    private readonly ConflictResolutionService _resolutionService;
    private readonly ILogger<ConflictResolutionController> _logger;

    public ConflictResolutionController(
        Window owner,
        SessionStateStore stateStore,
        IReadOnlyList<string> sessionNames,
        ConflictResolutionService resolutionService,
        ILogger<ConflictResolutionController> logger)
    {
        _owner = owner;
        _stateStore = stateStore;
        _sessionNames = sessionNames;
        _resolutionService = resolutionService;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _stateStore.Get();
        var pending = Flatten(_sessionNames, snapshot.Conflicts, snapshot.SessionStatuses);

        if (pending.Count == 0)
            return;

        if (pending.Count > MaxBatchSize)
        {
            _logger.LogWarning(
                "Refusing to start conflict resolution: {Count} pending conflict(s) exceeds the limit of {Limit}",
                pending.Count, MaxBatchSize);
            GenericMessageDialog.ShowInfo(
                _owner, _logger, "MutagenMon: resolve file conflict",
                "Too many conflicts. You can restart resolving or resolve manually.");
            return;
        }

        _logger.LogInformation("Starting conflict resolution batch: {Count} conflict(s)", pending.Count);

        for (var i = 0; i < pending.Count; i++)
        {
            var cancelled = await ResolveOneAsync(pending[i], i + 1, pending.Count, cancellationToken);
            if (cancelled)
            {
                _logger.LogInformation("Conflict resolution batch cancelled by the user");
                return;
            }
        }

        _logger.LogInformation("Conflict resolution batch complete");
    }

    /// <summary>Resolves one conflict, including its retry loop (a no-op
    /// visual merge re-presents the same conflict rather than advancing).
    /// Returns true if the user cancelled — aborting the whole batch is the
    /// caller's responsibility (FR-9.4).</summary>
    private async Task<bool> ResolveOneAsync(PendingConflict conflict, int count, int total, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (alphaStat, betaStat) = await ConnectingIndicatorWindow.RunAsync(_owner, async () =>
            {
                var alpha = await _resolutionService.StatAlphaAsync(conflict, cancellationToken);
                var beta = await _resolutionService.StatBetaAsync(conflict, cancellationToken);
                return (alpha, beta);
            });

            var defaultChoice = DefaultChoice(alphaStat, betaStat);
            var choice = ConflictResolutionWindow.Show(
                _owner, _logger, count, total, conflict.FileName, conflict.Alpha.Url, alphaStat, conflict.Beta.Url, betaStat, defaultChoice);

            if (choice is null)
                return true;

            if (choice == ConflictResolutionChoice.VisualMerge)
            {
                var preparation = await ConnectingIndicatorWindow.RunAsync(
                    _owner, () => _resolutionService.PrepareVisualMergeAsync(conflict, cancellationToken));

                await _resolutionService.RunMergeToolAsync(preparation, cancellationToken);

                var merged = await ConnectingIndicatorWindow.RunAsync(
                    _owner, () => _resolutionService.CompleteVisualMergeAsync(conflict, preparation, DateTimeOffset.UtcNow, cancellationToken));

                if (!merged)
                    continue;

                GenericMessageDialog.ShowInfo(
                    _owner, _logger, "MutagenMon: resolved file conflict",
                    $"Merged file copied to both sides:\n\n{conflict.FileName}");
                return false;
            }

            await ConnectingIndicatorWindow.RunAsync(
                _owner, () => _resolutionService.ResolveAsync(conflict, choice.Value, DateTimeOffset.UtcNow, cancellationToken));
            return false;
        }
    }

    /// <summary>Flattens every non-autoresolved conflict across
    /// <paramref name="sessionNames"/> (in that order) into a resolvable list,
    /// skipping any conflict whose session isn't currently reporting both
    /// endpoints (nothing to compare/copy against).</summary>
    private static IReadOnlyList<PendingConflict> Flatten(
        IReadOnlyCollection<string> sessionNames,
        IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflictsBySession,
        IReadOnlyDictionary<string, ParsedSessionStatus?> sessionStatuses)
    {
        var result = new List<PendingConflict>();
        foreach (var sessionName in sessionNames)
        {
            if (!conflictsBySession.TryGetValue(sessionName, out var conflicts))
                continue;
            sessionStatuses.TryGetValue(sessionName, out var status);
            if (status?.Alpha is null || status.Beta is null)
                continue;

            foreach (var conflict in conflicts)
            {
                if (conflict.AutoResolved)
                    continue;
                result.Add(new PendingConflict(sessionName, conflict.AlphaName, status.Alpha, status.Beta));
            }
        }

        return result;
    }

    /// <summary>Implements the default-selection rule (FR-9.3): prefer
    /// whichever side has the more recent modification time, defaulting to
    /// "B wins" on a tie.</summary>
    private static ConflictResolutionChoice DefaultChoice(FileStat alpha, FileStat beta) =>
        alpha.ModifiedUtc > beta.ModifiedUtc ? ConflictResolutionChoice.AWins : ConflictResolutionChoice.BWins;
}

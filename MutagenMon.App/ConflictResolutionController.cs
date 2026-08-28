using System.Windows;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>
/// Implements the batch resolution workflow (FR-9) — the batch
/// loop that presents each unresolved conflict in turn via
/// <see cref="ConflictResolutionWindow"/>, applies the chosen resolution
/// through <see cref="ConflictResolutionService"/>, and aborts the whole
/// batch the moment the user cancels (FR-9.4). Batch assembly and the
/// too-many-conflicts guard are <see cref="ConflictBatchPlanner"/>'s job
/// (FR-9.5); this class is purely the UI loop around it.
/// </summary>
public sealed class ConflictResolutionController
{
    private readonly Window _owner;
    private readonly ISessionStateStore _stateStore;
    private readonly IReadOnlyList<string> _sessionNames;
    private readonly ConflictResolutionService _resolutionService;
    private readonly ILogger<ConflictResolutionController> _logger;

    public ConflictResolutionController(
        Window owner,
        ISessionStateStore stateStore,
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
        var pending = ConflictBatchPlanner.Flatten(_sessionNames, snapshot.Conflicts, snapshot.SessionStatuses);

        if (pending.Count == 0)
            return;

        if (ConflictBatchPlanner.ExceedsBatchLimit(pending.Count))
        {
            _logger.LogWarning(
                "Refusing to start conflict resolution: {Count} pending conflict(s) exceeds the limit of {Limit}",
                pending.Count, ConflictBatchPlanner.MaxBatchSize);
            GenericMessageDialog.ShowInfo(
                _owner, "MutagenMon: resolve file conflict",
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

            var defaultChoice = ConflictBatchPlanner.DefaultChoice(alphaStat, betaStat);
            var choice = ConflictResolutionWindow.Show(
                _owner, count, total, conflict.FileName, conflict.Alpha.Url, alphaStat, conflict.Beta.Url, betaStat, defaultChoice);

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
                    _owner, "MutagenMon: resolved file conflict",
                    $"Merged file copied to both sides:\n\n{conflict.FileName}");
                return false;
            }

            await ConnectingIndicatorWindow.RunAsync(
                _owner, () => _resolutionService.ResolveAsync(conflict, choice.Value, DateTimeOffset.UtcNow, cancellationToken));
            return false;
        }
    }
}

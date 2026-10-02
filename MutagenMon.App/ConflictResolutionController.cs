using System.Windows;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Monitoring;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>
/// Assembles the manual resolution workflow (FR-9): flattens every
/// unresolved conflict into a list, applies the too-many-conflicts guard
/// (FR-9.5), and opens <see cref="ConflictResolutionWindow"/>, where the user
/// picks the conflicts to resolve one by one, in the order they are listed
/// (session order, then the order mutagen reports them).
/// </summary>
public sealed class ConflictResolutionController
{
    /// <summary>FR-9.5: refuse to open the resolution window above this many
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

    public Task RunAsync()
    {
        var snapshot = _stateStore.Get();
        var pending = Flatten(_sessionNames, snapshot.Conflicts, snapshot.SessionStatuses);

        if (pending.Count == 0)
            return Task.CompletedTask;

        if (pending.Count > MaxBatchSize)
        {
            _logger.LogWarning(
                "Refusing to open conflict resolution: {Count} pending conflict(s) exceeds the limit of {Limit}",
                pending.Count, MaxBatchSize);
            GenericMessageDialog.ShowInfo(
                _owner, _logger, "MutagenMon: resolve file conflict",
                "Too many conflicts. You can restart resolving or resolve manually.");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Showing conflict resolution window: {Count} conflict(s)", pending.Count);
        ConflictResolutionWindow.Show(_owner, _logger, _resolutionService, pending);
        _logger.LogInformation("Conflict resolution window closed");
        return Task.CompletedTask;
    }

    /// <summary>Flattens every non-autoresolved conflict across
    /// <paramref name="sessionNames"/> (in that order) into a resolvable list,
    /// skipping any conflict whose session isn't currently reporting both
    /// endpoints (nothing to compare/copy against).</summary>
    private static IReadOnlyList<ConflictItem> Flatten(
        IReadOnlyCollection<string> sessionNames,
        IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflictsBySession,
        IReadOnlyDictionary<string, ParsedSessionStatus?> sessionStatuses)
    {
        var result = new List<ConflictItem>();
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
                var pending = new PendingConflict(sessionName, conflict.AlphaName, status.Alpha, status.Beta);
                result.Add(new ConflictItem(pending, ConflictDescriber.Describe(conflict), ConflictDescriber.Details(conflict)));
            }
        }

        return result;
    }
}

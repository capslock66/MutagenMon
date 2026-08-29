using System.Text.RegularExpressions;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

/// <summary>Raised once a conflict has actually been auto-resolved
/// (FR-10.4's notification hook point — the actual desktop notification,
/// FR-11, is a later phase; this only lets a future subscriber wire one
/// up).</summary>
public sealed record AutoResolvedEventArgs(string SessionName, string FileName, string Rule);

/// <summary>
/// Implements automatic conflict resolution (FR-10). Run once per
/// poll, before the snapshot is published: every newly-seen conflict is
/// checked against the ordered <c>AutoResolve</c> rule list (first
/// regex match against the file name wins, FR-10.1/FR-10.2); once a
/// conflict (session + file name) has been through this once, its outcome
/// is cached for <c>AutoResolveHistoryAgeSeconds</c> so it isn't reprocessed —
/// and re-copied — every poll while mutagen keeps re-reporting it
/// (FR-10.3).
/// </summary>
public sealed class AutoResolveEngine
{
    private readonly IReadOnlyList<AutoResolveRule> _rules;
    private readonly TimeSpan _historyAge;
    private readonly ConflictResolutionService _resolutionService;
    private readonly Dictionary<string, (DateTimeOffset When, bool Resolved)> _history = new();

    public AutoResolveEngine(IReadOnlyList<AutoResolveRule> rules, TimeSpan historyAge, ConflictResolutionService resolutionService)
    {
        _rules = rules;
        _historyAge = historyAge;
        _resolutionService = resolutionService;
    }

    public event EventHandler<AutoResolvedEventArgs>? ConflictAutoResolved;

    /// <summary>Returns a new conflicts dictionary with
    /// <see cref="ConflictRecord.AutoResolved"/> flipped wherever a rule
    /// matched, ready to publish in place of the freshly-parsed one.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>>> ApplyAsync(
        IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> conflictsBySession,
        IReadOnlyDictionary<string, ParsedSessionStatus?> sessionStatuses,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        CleanHistory(nowUtc);

        if (conflictsBySession.Count == 0)
            return conflictsBySession;

        var result = new Dictionary<string, IReadOnlyList<ConflictRecord>>();
        foreach (var (sessionName, conflicts) in conflictsBySession)
        {
            sessionStatuses.TryGetValue(sessionName, out var status);
            var updated = new List<ConflictRecord>(conflicts.Count);
            foreach (var conflict in conflicts)
                updated.Add(await ApplyToOneAsync(sessionName, conflict, status, nowUtc, cancellationToken));
            result[sessionName] = updated;
        }
        return result;
    }

    /// <summary>Evicts history entries older than <c>AutoResolveHistoryAgeSeconds</c>.</summary>
    private void CleanHistory(DateTimeOffset nowUtc)
    {
        if (_history.Count == 0)
            return;

        foreach (var key in _history.Where(kvp => kvp.Value.When < nowUtc - _historyAge).Select(kvp => kvp.Key).ToList())
            _history.Remove(key);
    }

    private async Task<ConflictRecord> ApplyToOneAsync(
        string sessionName, ConflictRecord conflict, ParsedSessionStatus? status, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var key = $"{sessionName}:{conflict.AlphaName}";
        if (_history.TryGetValue(key, out var cached))
            return conflict with { AutoResolved = cached.Resolved };

        var resolved = await TryAutoResolveAsync(sessionName, conflict, status, nowUtc, cancellationToken);
        _history[key] = (nowUtc, resolved);
        return conflict with { AutoResolved = resolved };
    }

    /// <summary>First matching rule (unanchored
    /// regex search against the file name) wins and is applied immediately, no user
    /// interaction.</summary>
    private async Task<bool> TryAutoResolveAsync(
        string sessionName, ConflictRecord conflict, ParsedSessionStatus? status, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (status?.Alpha is null || status.Beta is null)
            return false;

        foreach (var rule in _rules)
        {
            if (string.IsNullOrEmpty(rule.FilePath) || !Regex.IsMatch(conflict.AlphaName, rule.FilePath))
                continue;

            var choice = rule.Resolve.StartsWith("B wins", StringComparison.OrdinalIgnoreCase)
                ? ConflictResolutionChoice.BWins
                : ConflictResolutionChoice.AWins;

            var pending = new PendingConflict(sessionName, conflict.AlphaName, status.Alpha, status.Beta);
            await _resolutionService.ResolveAsync(pending, choice, nowUtc, cancellationToken, automatic: true);

            ConflictAutoResolved?.Invoke(this, new AutoResolvedEventArgs(sessionName, conflict.AlphaName, rule.Resolve));
            return true;
        }

        return false;
    }
}

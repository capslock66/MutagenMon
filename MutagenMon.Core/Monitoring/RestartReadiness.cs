using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Monitoring;

/// <summary>
/// After a user-requested "Reload config & restart mutagen" (FR-7.1), the app waits
/// until every configured session has actually stopped reporting a status
/// before spawning the replacement process, so the restart doesn't race a
/// still-running `mutagen` session.
/// </summary>
public static class RestartReadiness
{
    public static bool AllSessionsStopped(
        IReadOnlyDictionary<string, ParsedSessionStatus?> statuses, IReadOnlyCollection<string> sessionNames)
    {
        foreach (var name in sessionNames)
            if (statuses.TryGetValue(name, out var status) && status is not null && !string.IsNullOrEmpty(status.Status))
                return false;

        return true;
    }
}

using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Status;

namespace MutagenMon.Core.Monitoring;

/// <summary>Immutable state published once per poll by
/// <see cref="SessionMonitorService"/>, read by the tray icon's UI timer.</summary>
public sealed record MonitorSnapshot(
    SessionStatusCode WorstCode,
    bool Enabled,
    bool ProfileJustUpdated,
    DateTimeOffset LastSuccessfulPollUtc,
    string RawLog,
    IReadOnlyDictionary<string, ParsedSessionStatus?> SessionStatuses,
    IReadOnlyDictionary<string, IReadOnlyList<ConflictRecord>> Conflicts,
    IReadOnlyDictionary<string, DateTimeOffset?> LastChangedUtc,
    IReadOnlyDictionary<string, SessionStatusCode> SessionCodes)
{
    public static MonitorSnapshot Initial(DateTimeOffset nowUtc, bool enabled) => new(
        SessionStatusCode.Unknown,
        enabled,
        false,
        nowUtc,
        "",
        new Dictionary<string, ParsedSessionStatus?>(),
        new Dictionary<string, IReadOnlyList<ConflictRecord>>(),
        new Dictionary<string, DateTimeOffset?>(),
        new Dictionary<string, SessionStatusCode>());
}

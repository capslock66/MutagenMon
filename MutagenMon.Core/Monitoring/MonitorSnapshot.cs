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
    IReadOnlyDictionary<string, SessionStatusCode> SessionCodes,
    DateTimeOffset PollStartedUtc = default)
{
    // PollStartedUtc: when the poll began, BEFORE `mutagen sync list` ran. LastSuccessfulPollUtc is taken after the
    // listing was parsed, so a listing can predate an event that finished in between (the end of a recovery, FR-60.3).
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

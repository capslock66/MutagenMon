namespace MutagenMon.Core.Status;

/// <summary>
/// Numeric values match the legacy session_code values exactly (FR-3) so
/// aggregation (FR-4) stays a plain Min() over the underlying ints — see
/// requirements/01-functional-requirements.md FR-3/FR-4.
/// </summary>
public enum SessionStatusCode
{
    ConnectionError = -2,
    NotRunning = -1,
    Unknown = 0,
    Scanning = 30,
    Syncing = 40,
    Problems = 50,
    Conflicts = 60,
    Ready = 100,
}

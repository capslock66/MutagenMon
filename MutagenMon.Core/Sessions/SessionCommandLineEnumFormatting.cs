namespace MutagenMon.Core.Sessions;

/// <summary>Kebab-case flag-value text for each enum mutagen accepts as a
/// literal string (e.g. `SyncMode.TwoWaySafe` -&gt; "two-way-safe").
/// Shared between <see cref="SessionCommandLineParser"/>'s Render and any
/// UI that needs to display the same values (the Add/Edit window's
/// comboboxes, FR-19 through FR-25) — one place mapping enum members to
/// the exact strings mutagen's CLI expects.</summary>
public static class SessionCommandLineEnumFormatting
{
    public static string ToFlagValue(this SyncMode mode) => mode switch
    {
        SyncMode.TwoWaySafe => "two-way-safe",
        SyncMode.TwoWayResolved => "two-way-resolved",
        SyncMode.OneWaySafe => "one-way-safe",
        SyncMode.OneWayReplica => "one-way-replica",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string ToFlagValue(this SymlinkMode mode) => mode switch
    {
        SymlinkMode.Portable => "portable",
        SymlinkMode.Ignore => "ignore",
        SymlinkMode.PosixRaw => "posix-raw",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string ToFlagValue(this WatchMode mode) => mode switch
    {
        WatchMode.Portable => "portable",
        WatchMode.ForcePoll => "force-poll",
        WatchMode.NoWatch => "no-watch",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string ToFlagValue(this ProbeMode mode) => mode switch
    {
        ProbeMode.Probe => "probe",
        ProbeMode.Assume => "assume",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string ToFlagValue(this ScanMode mode) => mode switch
    {
        ScanMode.Accelerated => "accelerated",
        ScanMode.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string ToFlagValue(this StageMode mode) => mode switch
    {
        StageMode.Mutagen => "mutagen",
        StageMode.Neighboring => "neighboring",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string ToFlagValue(this PermissionsMode mode) => mode switch
    {
        PermissionsMode.Portable => "portable",
        PermissionsMode.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

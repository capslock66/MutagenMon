namespace MutagenMon.Core.Sessions;

public enum SyncMode { TwoWaySafe, TwoWayResolved, OneWaySafe, OneWayReplica }

public enum SymlinkMode { Portable, Ignore, PosixRaw }

public enum WatchMode { Portable, ForcePoll, NoWatch }

public enum ProbeMode { Probe, Assume }

public enum ScanMode { Accelerated, Full }

public enum StageMode { Mutagen, Neighboring }

public enum PermissionsMode { Portable, Manual }

/// <summary>One value-type setting (an enum mode, or the polling interval)
/// mutagen lets be specified session-wide and/or overridden per endpoint
/// (e.g. `--watch-mode`/`-alpha`/`-beta`). Setting both <see
/// cref="Session"/> and <see cref="Alpha"/>/<see cref="Beta"/> at once is
/// allowed (FR-21.5) — `mutagen sync create --help` lists each as an
/// independent flag, with no documented mutual exclusion.
///
/// Constrained to <c>struct</c> deliberately: an unconstrained `T?` only
/// gives Nullable&lt;T&gt; semantics for reference types, not value types
/// (it's a compile-time-only nullable *annotation* there) — the `struct`
/// constraint is what actually makes `Session`/`Alpha`/`Beta` real
/// <see cref="Nullable{T}"/> fields for the enum/int cases this is used
/// for. String-valued settings use <see cref="PerSideText"/> instead.</summary>
public sealed record PerSide<T>(T? Session = default, T? Alpha = default, T? Beta = default) where T : struct;

/// <summary>String-valued counterpart to <see cref="PerSide{T}"/> (owner,
/// group, file/directory mode — FR-21).</summary>
public sealed record PerSideText(string? Session = null, string? Alpha = null, string? Beta = null);

/// <summary>A flag on the source line that FR-19 through FR-26 don't
/// recognize, preserved verbatim (FR-27.2) so nothing is silently lost.
/// <see cref="Keep"/> mirrors the "Unknown flags" checkbox state
/// (FR-27.3) — false means dropped when the line is regenerated.</summary>
public sealed record UnknownFlag(string Text, bool Keep = true);

/// <summary>Structured form of one `mutagen sync create` line (FR-18
/// through FR-27, requirements/07-session-management-requirements.md).
/// <see cref="SessionCommandLineParser"/> converts between this and the
/// raw line stored in `mutagen-create.bat`.</summary>
public sealed class SessionCommandLine
{
    public string Name { get; set; } = "";
    public string Alpha { get; set; } = "";
    public string Beta { get; set; } = "";

    public SyncMode Mode { get; set; } = SyncMode.TwoWaySafe;

    public List<string> Ignores { get; set; } = new();

    /// <summary>Tri-state (FR-20.5): null omits both `--ignore-vcs` and
    /// `--no-ignore-vcs` (defers to `~/.mutagen.yml`); true/false emits the
    /// corresponding flag.</summary>
    public bool? IgnoreVcs { get; set; }

    /// <summary>FR-20.6. false (default) omits `--ignore-syntax` (native
    /// mutagen syntax); true emits `--ignore-syntax=docker`.</summary>
    public bool IgnoreSyntaxDocker { get; set; }

    public PermissionsMode PermissionsMode { get; set; } = PermissionsMode.Portable;

    public PerSideText Owner { get; set; } = new();
    public PerSideText Group { get; set; } = new();
    public PerSideText FileMode { get; set; } = new();
    public PerSideText DirectoryMode { get; set; } = new();

    public SymlinkMode SymlinkMode { get; set; } = SymlinkMode.Portable;

    public PerSide<WatchMode> WatchMode { get; set; } = new();
    public PerSide<int> WatchPollingInterval { get; set; } = new();
    public PerSide<ProbeMode> ProbeMode { get; set; } = new();
    public PerSide<ScanMode> ScanMode { get; set; } = new();
    public PerSide<StageMode> StageMode { get; set; } = new();

    public string? MaxStagingFileSize { get; set; }
    public long? MaxEntryCount { get; set; }

    /// <summary>Flags found on the line but not recognized by any field
    /// above (FR-27.2/FR-27.3) — always non-null, empty when the line was
    /// fully understood.</summary>
    public List<UnknownFlag> UnknownFlags { get; set; } = new();
}

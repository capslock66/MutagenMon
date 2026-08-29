using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MutagenMon.Core.Configuration;

/// <summary>
/// Mirrors the key names and defaults of the legacy
/// config_mutagenmon.json exactly (see
/// requirements/06-configuration-reference.md) so operators
/// migrating from the legacy app don't have to relearn tuning knobs.
/// Property names ARE the JSON key names (case-sensitive) — no
/// <see cref="JsonPropertyNameAttribute"/> needed here.
/// </summary>
public sealed class MutagenMonOptions
{
    /// <summary>Legacy 0-100 verbosity dial — kept only for config-file
    /// compatibility with the legacy app; has no effect. Use
    /// <see cref="MinLogLevel"/> to actually control verbosity.</summary>
    public int DebugLevel { get; set; }

    /// <summary>Minimum level written to log/mutagenMon.log (Trace,
    /// Debug, Information, Warning, Error, Critical, or None). Applies only
    /// once this config has been loaded — everything up to that point (the
    /// fragile early-startup window, before the log path itself is even
    /// known) is always logged in full, regardless of this setting, so a
    /// startup failure is never silently dropped for having configured too
    /// high a level.</summary>
    public LogLevel MinLogLevel { get; set; } = LogLevel.Trace;

    public bool DebugExceptionsToConsole { get; set; }

    public bool NotifyRestartConnection { get; set; }

    /// <summary>Notify when conflicts are detected.</summary>
    public bool NotifyConflicts { get; set; } = true;

    /// <summary>Notify when conflicts are autoresolved.</summary>
    public bool NotifyAutoresolve { get; set; } = true;

    /// <summary>If MutagenMon should start enabled (enabled means that it
    /// restarts mutagen sessions if they have errors or are not
    /// running).</summary>
    public bool StartEnabled { get; set; } = true;

    /// <summary>Path to the external visual merge tool binary.</summary>
    public string MergePath { get; set; } = "";

    /// <summary>Path to the scp binary, used for remote (SSH) endpoint
    /// file transfers.</summary>
    public string ScpPath { get; set; } = "";

    /// <summary>Path to the ssh binary, used for remote (SSH) endpoint
    /// commands.</summary>
    public string SshPath { get; set; } = "";

    /// <summary>Path to the mutagen binary.</summary>
    public string MutagenPath { get; set; } = "mutagen/mutagen";

    public string TrayTooltip { get; set; } = "MutagenMon";

    /// <summary>Path for log files.</summary>
    public string LogPath { get; set; } = "log";

    /// <summary>Path to mutagen sessions config.</summary>
    public string MutagenSessionsBatFile { get; set; } = "mutagen/mutagen-create.bat";

    /// <summary>Number of pollings with "not connected" errors to allow
    /// before restarting a mutagen session.</summary>
    public int SessionMaxErrors { get; set; } = 30000;

    /// <summary>Number of pollings with no session found to allow before
    /// restarting a mutagen session.</summary>
    public int SessionMaxNoSession { get; set; } = 200;

    /// <summary>Number of pollings with duplicate session found to allow
    /// before restarting mutagen sessions.</summary>
    public int SessionMaxDuplicate { get; set; } = 10000;

    /// <summary>Number of milliseconds to wait between polling
    /// "mutagen sync list".</summary>
    public int MutagenPollPeriodMs { get; set; } = 1000;

    /// <summary>Number of seconds to allow for status lag before changing
    /// the tray icon to "stale" and restarting MutagenMon.</summary>
    public StatusMaxLagOptions StatusMaxLag { get; set; } = new();

    /// <summary>Set to the mutagen directory with caches and archives.</summary>
    public string MutagenProfileDir { get; set; } = "";

    /// <summary>Watch the mutagen profile dir for session updates (in
    /// seconds, or 0 to disable).</summary>
    public int MutagenProfileDirWatchPeriod { get; set; } = 1;

    /// <summary>Ignore more frequent session updates than this (in
    /// seconds).</summary>
    public int MutagenProfileGraceSeconds { get; set; } = 4;

    /// <summary>Show notifications for mutagen session profile updates.</summary>
    public bool NotifyMutagenProfileUpdate { get; set; }

    /// <summary>Ordered list of rules matching conflicting file paths — see
    /// <see cref="AutoResolveRule"/> for what each entry means.</summary>
    public List<AutoResolveRule> AutoResolve { get; set; } = new();

    /// <summary>How long to remember autoresolved conflicts, so they
    /// aren't autoresolved again (in seconds).</summary>
    public int AutoResolveHistoryAgeSeconds { get; set; } = 30;
}

public sealed class StatusMaxLagOptions
{
    [JsonPropertyName("Info")]
    public int InfoSeconds { get; set; } = 4;

    [JsonPropertyName("Warning")]
    public int WarningSeconds { get; set; } = 15;

    [JsonPropertyName("Error")]
    public int ErrorSeconds { get; set; } = 50;

    [JsonPropertyName("Restart")]
    public int RestartSeconds { get; set; } = 90;

    public Status.LagThresholds ToLagThresholds() => new(
        TimeSpan.FromSeconds(InfoSeconds),
        TimeSpan.FromSeconds(WarningSeconds),
        TimeSpan.FromSeconds(ErrorSeconds),
        TimeSpan.FromSeconds(RestartSeconds));
}

public sealed class AutoResolveRule
{
    /// <summary>Regular expression matched, unanchored, against the whole
    /// conflicting path (directory and filename).</summary>
    [JsonPropertyName("filepath")]
    public string FilePath { get; set; } = "";

    /// <summary>"A wins" or "B wins" — the resolution to apply automatically
    /// when <see cref="FilePath"/> matches.</summary>
    [JsonPropertyName("resolve")]
    public string Resolve { get; set; } = "";
}

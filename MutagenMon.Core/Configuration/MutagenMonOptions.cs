using System.Text.Json.Serialization;

namespace MutagenMon.Core.Configuration;

/// <summary>
/// Mirrors the key names and defaults of the legacy
/// config_mutagenmon.json exactly (see
/// requirements/06-configuration-reference.md) so operators
/// migrating from the legacy app don't have to relearn tuning knobs.
/// </summary>
public sealed class MutagenMonOptions
{
    [JsonPropertyName("DEBUG_LEVEL")]
    public int DebugLevel { get; set; }

    [JsonPropertyName("DEBUG_EXCEPTIONS_TO_CONSOLE")]
    public bool DebugExceptionsToConsole { get; set; }

    [JsonPropertyName("NOTIFY_RESTART_CONNECTION")]
    public bool NotifyRestartConnection { get; set; }

    [JsonPropertyName("NOTIFY_CONFLICTS")]
    public bool NotifyConflicts { get; set; } = true;

    [JsonPropertyName("NOTIFY_AUTORESOLVE")]
    public bool NotifyAutoresolve { get; set; } = true;

    [JsonPropertyName("START_ENABLED")]
    public bool StartEnabled { get; set; } = true;

    [JsonPropertyName("MERGE_PATH")]
    public string MergePath { get; set; } = "";

    [JsonPropertyName("SCP_PATH")]
    public string ScpPath { get; set; } = "";

    [JsonPropertyName("SSH_PATH")]
    public string SshPath { get; set; } = "";

    [JsonPropertyName("MUTAGEN_PATH")]
    public string MutagenPath { get; set; } = "mutagen/mutagen";

    [JsonPropertyName("TRAY_TOOLTIP")]
    public string TrayTooltip { get; set; } = "MutagenMon";

    [JsonPropertyName("LOG_PATH")]
    public string LogPath { get; set; } = "log";

    [JsonPropertyName("MUTAGEN_SESSIONS_BAT_FILE")]
    public string MutagenSessionsBatFile { get; set; } = "mutagen/mutagen-create.bat";

    [JsonPropertyName("SESSION_MAX_ERRORS")]
    public int SessionMaxErrors { get; set; } = 30000;

    [JsonPropertyName("SESSION_MAX_NOSESSION")]
    public int SessionMaxNoSession { get; set; } = 200;

    [JsonPropertyName("SESSION_MAX_DUPLICATE")]
    public int SessionMaxDuplicate { get; set; } = 10000;

    [JsonPropertyName("MUTAGEN_POLL_PERIOD")]
    public int MutagenPollPeriodMs { get; set; } = 1000;

    [JsonPropertyName("STATUS_MAX_LAG")]
    public StatusMaxLagOptions StatusMaxLag { get; set; } = new();

    [JsonPropertyName("MUTAGEN_PROFILE_DIR")]
    public string MutagenProfileDir { get; set; } = "";

    [JsonPropertyName("MUTAGEN_PROFILE_DIR_WATCH_PERIOD")]
    public int MutagenProfileDirWatchPeriod { get; set; } = 1;

    [JsonPropertyName("MUTAGEN_PROFILE_GRACE")]
    public int MutagenProfileGraceSeconds { get; set; } = 4;

    [JsonPropertyName("NOTIFY_MUTAGEN_PROFILE_UPDATE")]
    public bool NotifyMutagenProfileUpdate { get; set; }

    [JsonPropertyName("AUTORESOLVE")]
    public List<AutoResolveRule> AutoResolve { get; set; } = new();

    [JsonPropertyName("AUTORESOLVE_HISTORY_AGE")]
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
    [JsonPropertyName("filepath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("resolve")]
    public string Resolve { get; set; } = "";
}

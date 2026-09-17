namespace MutagenMon.Core.Status;

/// <summary>Icon-facing staleness tiers (FR-6.2). Deliberately excludes the
/// "Restart" threshold — exceeding it doesn't produce an icon state, it
/// triggers a full self-restart (FR-6.3/TIC-10) instead, checked separately
/// (<c>TrayIconController.IsBeyondRestartThreshold</c>).</summary>
public enum StalenessTier
{
    None,
    Info,
    Warning,
    Error,
}

/// <summary>Mirrors config_mutagenmon.json's StatusMaxLag.</summary>
public sealed record LagThresholds(TimeSpan Info, TimeSpan Warning, TimeSpan Error, TimeSpan Restart);

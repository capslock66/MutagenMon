namespace MutagenMon.Core.Status;

/// <summary>Icon-facing staleness tiers (FR-6.2). Deliberately excludes the
/// "Restart" threshold — exceeding it doesn't produce an icon state, it
/// triggers a full self-restart (FR-6.3/TIC-10) instead, checked separately
/// via <see cref="StalenessCalculator.IsBeyondRestartThreshold"/>.</summary>
public enum StalenessTier
{
    None,
    Info,
    Warning,
    Error,
}

/// <summary>Mirrors config_mutagenmon.json's StatusMaxLag.</summary>
public sealed record LagThresholds(TimeSpan Info, TimeSpan Warning, TimeSpan Error, TimeSpan Restart);

/// <summary>Implements the staleness checks (FR-6.1/6.2/6.3) as
/// pure functions of (last successful poll, now, thresholds).</summary>
public static class StalenessCalculator
{
    public static StalenessTier GetTier(DateTimeOffset lastSuccessfulPollUtc, DateTimeOffset nowUtc, LagThresholds thresholds)
    {
        var age = nowUtc - lastSuccessfulPollUtc;
        if (age > thresholds.Error)
            return StalenessTier.Error;
        if (age > thresholds.Warning)
            return StalenessTier.Warning;
        if (age > thresholds.Info)
            return StalenessTier.Info;
        return StalenessTier.None;
    }

    public static bool IsBeyondRestartThreshold(DateTimeOffset lastSuccessfulPollUtc, DateTimeOffset nowUtc, LagThresholds thresholds)
        => (nowUtc - lastSuccessfulPollUtc) > thresholds.Restart;
}

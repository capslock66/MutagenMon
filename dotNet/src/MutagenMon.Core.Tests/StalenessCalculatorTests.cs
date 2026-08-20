using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

public class StalenessCalculatorTests
{
    private static readonly LagThresholds Thresholds = new(
        Info: TimeSpan.FromSeconds(4),
        Warning: TimeSpan.FromSeconds(15),
        Error: TimeSpan.FromSeconds(50),
        Restart: TimeSpan.FromSeconds(90));

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, StalenessTier.None)]
    [InlineData(3, StalenessTier.None)]
    [InlineData(4, StalenessTier.None)] // strictly greater-than, not >=
    [InlineData(5, StalenessTier.Info)]
    [InlineData(14, StalenessTier.Info)]
    [InlineData(15, StalenessTier.Info)]
    [InlineData(16, StalenessTier.Warning)]
    [InlineData(49, StalenessTier.Warning)]
    [InlineData(50, StalenessTier.Warning)]
    [InlineData(51, StalenessTier.Error)]
    [InlineData(89, StalenessTier.Error)]
    public void TierBoundariesMatchConfiguredThresholds(int ageSeconds, StalenessTier expected)
    {
        var now = Epoch + TimeSpan.FromSeconds(ageSeconds);
        Assert.Equal(expected, StalenessCalculator.GetTier(Epoch, now, Thresholds));
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(91, true)]
    public void RestartThresholdIsCheckedSeparatelyFromTheIconTier(int ageSeconds, bool expectedBeyond)
    {
        var now = Epoch + TimeSpan.FromSeconds(ageSeconds);
        Assert.Equal(expectedBeyond, StalenessCalculator.IsBeyondRestartThreshold(Epoch, now, Thresholds));
        // Still reported as the Error icon tier even once restart-eligible — the
        // self-restart watchdog is a separate check, not a 5th icon tier (TIC-10).
        Assert.Equal(StalenessTier.Error, StalenessCalculator.GetTier(Epoch, now, Thresholds));
    }
}

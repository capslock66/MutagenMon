using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>
/// Parametrized over every row of requirements/03-tray-icon-requirements.md
/// §3's decision table — this test IS the executable form of that table.
/// </summary>
public class TrayIconStateResolverTests
{
    private const string AppName = "MutagenMon";

    [Theory]
    [MemberData(nameof(Rows))]
    public void ResolvesToTheExpectedIconAndTooltip(
        SessionStatusCode worstCode, bool enabled, bool profileJustUpdated, StalenessTier staleness,
        string expectedIconKey, string expectedTooltipSuffix)
    {
        var input = new TrayIconInput(worstCode, enabled, profileJustUpdated, staleness);
        var result = TrayIconStateResolver.Resolve(input, AppName);

        Assert.Equal(expectedIconKey, result.IconKey);
        Assert.Equal($"{AppName}: {expectedTooltipSuffix}", result.Tooltip);
    }

    public static TheoryData<SessionStatusCode, bool, bool, StalenessTier, string, string> Rows() => new()
    {
        // code 0 — waiting for first status
        { SessionStatusCode.Unknown, true, false, StalenessTier.None, "lightgray-init", "waiting for status..." },

        // code 100 — ready
        { SessionStatusCode.Ready, true, false, StalenessTier.None, "green", "mutagen is watching for changes" },
        { SessionStatusCode.Ready, true, true, StalenessTier.None, "green-success", "mutagen is watching for changes (updated)" },
        { SessionStatusCode.Ready, false, false, StalenessTier.None, "green-stop", "mutagen is stopping" },
        { SessionStatusCode.Ready, false, true, StalenessTier.None, "green-stop", "mutagen is stopping" },

        // code 60 — conflicts
        { SessionStatusCode.Conflicts, true, false, StalenessTier.None, "green-conflict", "conflicts" },

        // code 50 — problems
        { SessionStatusCode.Problems, true, false, StalenessTier.None, "green-error", "problems" },

        // code 40 — syncing
        { SessionStatusCode.Syncing, true, false, StalenessTier.None, "green-sync", "mutagen is syncing" },
        { SessionStatusCode.Syncing, true, true, StalenessTier.None, "green-success", "mutagen is syncing (updated)" },

        // code 30 — scanning
        { SessionStatusCode.Scanning, true, false, StalenessTier.None, "green-scan", "mutagen is scanning" },
        { SessionStatusCode.Scanning, true, true, StalenessTier.None, "green-success", "mutagen is scanning (updated)" },

        // staleness tiers — same 3 icons regardless of base state, but tooltip keeps
        // the base state's own description (fixed quirk #2).
        { SessionStatusCode.Ready, true, false, StalenessTier.Info, "green-timeout-white", "mutagen is watching for changes (stale)" },
        { SessionStatusCode.Ready, true, false, StalenessTier.Warning, "green-timeout", "mutagen is watching for changes (stale)" },
        { SessionStatusCode.Ready, true, false, StalenessTier.Error, "green-timeout-red", "mutagen is watching for changes (stale)" },
        { SessionStatusCode.Conflicts, true, false, StalenessTier.Warning, "green-timeout", "conflicts (stale)" },
        { SessionStatusCode.Problems, true, false, StalenessTier.Error, "green-timeout-red", "problems (stale)" },
        { SessionStatusCode.Syncing, true, false, StalenessTier.Info, "green-timeout-white", "mutagen is syncing (stale)" },
        { SessionStatusCode.Scanning, true, false, StalenessTier.Warning, "green-timeout", "mutagen is scanning (stale)" },

        // code -1 — not running
        { SessionStatusCode.NotRunning, true, false, StalenessTier.None, "darkgray-restart", "mutagen is not running (starting)" },
        { SessionStatusCode.NotRunning, false, false, StalenessTier.None, "darkgray", "mutagen is not running (disabled)" },

        // code -2 — cannot connect
        { SessionStatusCode.ConnectionError, true, false, StalenessTier.None, "orange-restart", "error (starting)" },
        { SessionStatusCode.ConnectionError, false, false, StalenessTier.None, "orange", "error (disabled)" },
    };

    [Fact]
    public void StalenessTakesPriorityOverTheUpdatedFlash()
    {
        var input = new TrayIconInput(SessionStatusCode.Ready, Enabled: true, ProfileJustUpdated: true, StalenessTier.Warning);
        var result = TrayIconStateResolver.Resolve(input, AppName);
        Assert.Equal("green-timeout", result.IconKey);
        Assert.DoesNotContain("updated", result.Tooltip);
    }

    [Fact]
    public void DisabledStoppingTakesPriorityOverStaleness()
    {
        var input = new TrayIconInput(SessionStatusCode.Ready, Enabled: false, ProfileJustUpdated: false, StalenessTier.Error);
        var result = TrayIconStateResolver.Resolve(input, AppName);
        Assert.Equal("green-stop", result.IconKey);
    }

    [Fact]
    public void ConflictsOutrankProblemsAndSyncingForTheUpdatedFlash()
    {
        // Conflicts/Problems are not in the "updated flash eligible" set at all —
        // confirms the fix stays scoped to Ready/Syncing/Scanning only.
        var input = new TrayIconInput(SessionStatusCode.Conflicts, Enabled: true, ProfileJustUpdated: true, StalenessTier.None);
        var result = TrayIconStateResolver.Resolve(input, AppName);
        Assert.Equal("green-conflict", result.IconKey);
        Assert.DoesNotContain("updated", result.Tooltip);
    }
}

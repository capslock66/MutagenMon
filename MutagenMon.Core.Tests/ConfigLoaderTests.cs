using MutagenMon.Core.Configuration;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ConfigLoaderTests
{
    private const string SampleConfig = """
        {
        # Set to 0 to disable logging to log/debug.log
        "DEBUG_LEVEL": 0,

        "NOTIFY_CONFLICTS": true,
        "START_ENABLED": true,
        "MUTAGEN_PATH": "mutagen\\mutagen",
        "TRAY_TOOLTIP": "MutagenMon",
        "MUTAGEN_SESSIONS_BAT_FILE": "mutagen/mutagen-create.bat",
        "SESSION_MAX_ERRORS": 30000,
        "MUTAGEN_POLL_PERIOD": 1000,
        "STATUS_MAX_LAG": {"Info": 4, "Warning": 15, "Error": 50, "Restart": 90},
        "MUTAGEN_PROFILE_DIR": "C:\\Users\\me\\.mutagen",
        "MUTAGEN_PROFILE_DIR_WATCH_PERIOD": 1,
        "MUTAGEN_PROFILE_GRACE": 4,

        # Add records matching filenames:
        "AUTORESOLVE": [
            {
                "filepath": "/\\.idea/",
                "resolve": "A wins"
            }
        ],
        "AUTORESOLVE_HISTORY_AGE": 30
        }
        """;

    [Fact]
    public void ParsesCommentedJsonIntoStronglyTypedOptions()
    {
        var options = ConfigLoader.ParseText(SampleConfig);

        Assert.Equal(0, options.DebugLevel);
        Assert.True(options.NotifyConflicts);
        Assert.True(options.StartEnabled);
        Assert.Equal(1000, options.MutagenPollPeriodMs);
        Assert.Equal("MutagenMon", options.TrayTooltip);
    }

    [Fact]
    public void ParsesNestedStatusMaxLagObject()
    {
        var options = ConfigLoader.ParseText(SampleConfig);

        Assert.Equal(4, options.StatusMaxLag.InfoSeconds);
        Assert.Equal(15, options.StatusMaxLag.WarningSeconds);
        Assert.Equal(50, options.StatusMaxLag.ErrorSeconds);
        Assert.Equal(90, options.StatusMaxLag.RestartSeconds);

        var thresholds = options.StatusMaxLag.ToLagThresholds();
        Assert.Equal(TimeSpan.FromSeconds(50), thresholds.Error);
    }

    [Fact]
    public void ParsesAutoResolveRulesArray()
    {
        var options = ConfigLoader.ParseText(SampleConfig);

        Assert.Single(options.AutoResolve);
        Assert.Equal("A wins", options.AutoResolve[0].Resolve);
        Assert.Contains(".idea", options.AutoResolve[0].FilePath);
    }

    [Fact]
    public void CommentLinesDoNotBreakParsing()
    {
        const string withHeavyComments = """
            {
            # comment 1
            # comment 2
            "DEBUG_LEVEL": 5
            # trailing comment
            }
            """;
        var options = ConfigLoader.ParseText(withHeavyComments);
        Assert.Equal(5, options.DebugLevel);
    }
}

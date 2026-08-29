using Microsoft.Extensions.Logging;
using MutagenMon.Core.Configuration;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ConfigLoaderTests
{
    private const string SampleConfig = """
        {
        # Set to 0 to disable logging to log/debug.log
        "DebugLevel": 0,

        "NotifyConflicts": true,
        "StartEnabled": true,
        "MutagenPath": "mutagen\\mutagen",
        "TrayTooltip": "MutagenMon",
        "MutagenSessionsBatFile": "mutagen/mutagen-create.bat",
        "SessionMaxErrors": 30000,
        "MutagenPollPeriodMs": 1000,
        "StatusMaxLag": {"Info": 4, "Warning": 15, "Error": 50, "Restart": 90},
        "MutagenProfileDir": "C:\\Users\\me\\.mutagen",
        "MutagenProfileDirWatchPeriod": 1,
        "MutagenProfileGraceSeconds": 4,

        # Add records matching filenames:
        "AutoResolve": [
            {
                "filepath": "/\\.idea/",
                "resolve": "A wins"
            }
        ],
        "AutoResolveHistoryAgeSeconds": 30
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
    public void DefaultsMinLogLevelToTraceWhenAbsent()
    {
        var options = ConfigLoader.ParseText(SampleConfig);

        Assert.Equal(LogLevel.Trace, options.MinLogLevel);
    }

    [Fact]
    public void ParsesMinLogLevelFromItsStringName()
    {
        const string withMinLogLevel = """
            {
            "MinLogLevel": "Warning"
            }
            """;

        var options = ConfigLoader.ParseText(withMinLogLevel);

        Assert.Equal(LogLevel.Warning, options.MinLogLevel);
    }

    [Fact]
    public void CommentLinesDoNotBreakParsing()
    {
        const string withHeavyComments = """
            {
            # comment 1
            # comment 2
            "DebugLevel": 5
            # trailing comment
            }
            """;
        var options = ConfigLoader.ParseText(withHeavyComments);
        Assert.Equal(5, options.DebugLevel);
    }
}

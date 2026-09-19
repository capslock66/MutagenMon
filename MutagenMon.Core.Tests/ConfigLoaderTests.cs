using MutagenMon.Core.Configuration;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void StripsWholeLineCommentsBeforeParsing()
    {
        var options = ConfigLoader.Parse("""
            # this is a comment
            {
              "TrayTooltip": "MyTooltip",
              # another comment
              "MutagenPollPeriodMs": 2000
            }
            """);

        Assert.Equal("MyTooltip", options.TrayTooltip);
        Assert.Equal(2000, options.MutagenPollPeriodMs);
    }

    [Fact]
    public void ParsesNestedStatusMaxLagAndAutoResolveRules()
    {
        var options = ConfigLoader.Parse("""
            {
              "StatusMaxLag": { "Info": 1, "Warning": 2, "Error": 3, "Restart": 4 },
              "AutoResolve": [ { "filepath": "nohup\\.out$", "resolve": "A wins" } ]
            }
            """);

        Assert.Equal(1, options.StatusMaxLag.InfoSeconds);
        Assert.Equal(4, options.StatusMaxLag.RestartSeconds);
        Assert.Single(options.AutoResolve);
        Assert.Equal("A wins", options.AutoResolve[0].Resolve);
    }

    [Fact]
    public void SerializeThenParseRoundTrips()
    {
        var original = new MutagenMonOptions { TrayTooltip = "RoundTrip", MutagenPollPeriodMs = 1500 };
        original.AutoResolve.Add(new AutoResolveRule { FilePath = ".*\\.tmp$", Resolve = "B wins" });

        var reparsed = ConfigLoader.Parse(ConfigLoader.Serialize(original));

        Assert.Equal("RoundTrip", reparsed.TrayTooltip);
        Assert.Equal(1500, reparsed.MutagenPollPeriodMs);
        Assert.Single(reparsed.AutoResolve);
        Assert.Equal("B wins", reparsed.AutoResolve[0].Resolve);
    }

    [Fact]
    public void SaveThenLoadRoundTripsThroughDisk()
    {
        var path = Path.GetTempFileName();
        try
        {
            var original = new MutagenMonOptions { TrayTooltip = "OnDisk" };
            ConfigLoader.Save(path, original);

            var loaded = ConfigLoader.Load(path);

            Assert.Equal("OnDisk", loaded.TrayTooltip);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

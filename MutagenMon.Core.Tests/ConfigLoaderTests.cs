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
        original.SshServers.Add(new SshServerEntry { Host = "robbie" });

        var reparsed = ConfigLoader.Parse(ConfigLoader.Serialize(original));

        Assert.Equal("RoundTrip", reparsed.TrayTooltip);
        Assert.Equal(1500, reparsed.MutagenPollPeriodMs);
        Assert.Single(reparsed.AutoResolve);
        Assert.Equal("B wins", reparsed.AutoResolve[0].Resolve);
        Assert.Single(reparsed.SshServers);
        Assert.Equal("robbie", reparsed.SshServers[0].Host);
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

    [Fact]
    public void WindowSizeDefaultsMatchTheShippedWindow()
    {
        var options = ConfigLoader.Parse("{ }");

        Assert.Equal(900, options.StatusWindowWidth);
        Assert.Equal(480, options.StatusWindowHeight);
    }

    [Fact]
    public void ApplyWindowSizeReplacesExistingValuesAndKeepsEverythingElse()
    {
        var text = "# keep me\n{\n  \"StatusWindowWidth\": 100,\n  \"TrayTooltip\": \"x\",\n  \"StatusWindowHeight\": 200\n}\n";

        var result = ConfigLoader.ApplyWindowSize(text, 1000, 700);

        Assert.Equal("# keep me\n{\n  \"StatusWindowWidth\": 1000,\n  \"TrayTooltip\": \"x\",\n  \"StatusWindowHeight\": 700\n}\n", result);
    }

    [Fact]
    public void ApplyWindowSizeInsertsMissingKeysAfterTheOpeningBrace()
    {
        var result = ConfigLoader.ApplyWindowSize("# comment\n{\n  \"TrayTooltip\": \"x\"\n}\n", 1000, 700);

        var options = ConfigLoader.Parse(result);
        Assert.Equal(1000, options.StatusWindowWidth);
        Assert.Equal(700, options.StatusWindowHeight);
        Assert.Equal("x", options.TrayTooltip);
        Assert.StartsWith("# comment\n{", result);
    }

    [Fact]
    public void ApplyWindowSizeHandlesAnEmptyObjectAndCrLf()
    {
        var result = ConfigLoader.ApplyWindowSize("{\r\n}\r\n", 1000, 700);

        var options = ConfigLoader.Parse(result);
        Assert.Equal(1000, options.StatusWindowWidth);
        Assert.Equal(700, options.StatusWindowHeight);
    }

    [Fact]
    public void ApplyWindowSizeIgnoresACommentedKey()
    {
        var result = ConfigLoader.ApplyWindowSize("# \"StatusWindowWidth\": 1\n{\n}\n", 1000, 700);

        Assert.StartsWith("# \"StatusWindowWidth\": 1\n", result);
        Assert.Equal(1000, ConfigLoader.Parse(result).StatusWindowWidth);
    }

    [Fact]
    public void SaveWindowSizeRewritesTheFileInPlace()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# keep\n{\n  \"TrayTooltip\": \"x\"\n}\n");

            ConfigLoader.SaveWindowSize(path, 1111, 555);

            var text = File.ReadAllText(path);
            Assert.StartsWith("# keep\n", text);
            Assert.Equal(1111, ConfigLoader.Load(path).StatusWindowWidth);
            Assert.Equal(555, ConfigLoader.Load(path).StatusWindowHeight);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using MutagenMon.Core.Configuration;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-66.1: the update-detection keys are added to the config file when absent.</summary>
public class ConfigLoaderUpdateKeysTests
{
    [Fact]
    public void AddsBothKeysWithTheirDefaults()
    {
        var text = ConfigLoader.ApplyMissingUpdateKeys("{\n  \"TrayTooltip\": \"X\"\n}", out var added);

        Assert.Equal(["DeployPath", "NotifyUpdateAvailable"], added);
        var options = ConfigLoader.Parse(text);
        Assert.Equal("", options.DeployPath);
        Assert.True(options.NotifyUpdateAvailable);
        Assert.Equal("X", options.TrayTooltip);
    }

    [Fact]
    public void KeepsAnExistingValueAndTheComments()
    {
        const string original = "# header\n{\n  \"DeployPath\": \"\\\\\\\\srv\\\\share\"\n}\n";

        var text = ConfigLoader.ApplyMissingUpdateKeys(original, out var added);

        Assert.Equal(["NotifyUpdateAvailable"], added);
        Assert.Contains("# header", text);
        Assert.Equal("\\\\srv\\share", ConfigLoader.Parse(text).DeployPath);
    }

    [Fact]
    public void EnsureUpdateKeysRewritesTheFileOnlyWhenAKeyIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutagenmon-config-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\n  \"TrayTooltip\": \"X\"\n}");
        try
        {
            var first = ConfigLoader.EnsureUpdateKeys(path);
            var textAfterFirst = File.ReadAllText(path);
            var second = ConfigLoader.EnsureUpdateKeys(path);

            Assert.Equal(2, first.Count);
            Assert.Empty(second);
            Assert.Equal(textAfterFirst, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using MutagenMon.Core.Configuration;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-63.1: the halted-session keys are added to the config file when absent.</summary>
public class ConfigLoaderHaltedKeysTests
{
    private static readonly string[] AllKeys =
    [
        "HaltedOneSidedRootEmptying",
        "HaltedOneSidedRootEmptyingAutoFix",
        "HaltedOneSidedRootEmptyingCooldownSeconds",
    ];

    [Fact]
    public void AddsTheThreeKeysWithTheirDefaultsInDeclaredOrder()
    {
        var text = ConfigLoader.ApplyMissingHaltedKeys("{\n  \"TrayTooltip\": \"X\"\n}", out var added);

        Assert.Equal(AllKeys, added);
        var options = ConfigLoader.Parse(text);
        Assert.Equal("Halted due to one-sided root emptying", options.HaltedOneSidedRootEmptying);
        Assert.False(options.HaltedOneSidedRootEmptyingAutoFix);
        Assert.Equal(60, options.HaltedOneSidedRootEmptyingCooldownSeconds);
        Assert.Equal("X", options.TrayTooltip);
        Assert.True(text.IndexOf(AllKeys[0], StringComparison.Ordinal) < text.IndexOf(AllKeys[1], StringComparison.Ordinal));
        Assert.True(text.IndexOf(AllKeys[1], StringComparison.Ordinal) < text.IndexOf(AllKeys[2], StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsTheWholeLineComments()
    {
        const string original = "# header comment\n{\n  # a comment before a key\n  \"TrayTooltip\": \"X\"\n}\n";

        var text = ConfigLoader.ApplyMissingHaltedKeys(original, out _);

        Assert.Contains("# header comment", text);
        Assert.Contains("# a comment before a key", text);
        Assert.Equal("X", ConfigLoader.Parse(text).TrayTooltip);
    }

    [Fact]
    public void NeverModifiesAKeyThatIsAlreadyPresent()
    {
        const string original = """
            {
              "HaltedOneSidedRootEmptying": "my custom text",
              "HaltedOneSidedRootEmptyingAutoFix": true
            }
            """;

        var text = ConfigLoader.ApplyMissingHaltedKeys(original, out var added);

        Assert.Equal(["HaltedOneSidedRootEmptyingCooldownSeconds"], added);
        var options = ConfigLoader.Parse(text);
        Assert.Equal("my custom text", options.HaltedOneSidedRootEmptying);
        Assert.True(options.HaltedOneSidedRootEmptyingAutoFix);
    }

    [Fact]
    public void IsIdempotent()
    {
        var once = ConfigLoader.ApplyMissingHaltedKeys("{\n  \"TrayTooltip\": \"X\"\n}", out _);

        var twice = ConfigLoader.ApplyMissingHaltedKeys(once, out var added);

        Assert.Empty(added);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ACommentedOutKeyDoesNotCountAsPresent()
    {
        var text = ConfigLoader.ApplyMissingHaltedKeys(
            "{\n  # \"HaltedOneSidedRootEmptyingCooldownSeconds\": 5,\n  \"TrayTooltip\": \"X\"\n}", out var added);

        Assert.Contains("HaltedOneSidedRootEmptyingCooldownSeconds", added);
        Assert.Equal(60, ConfigLoader.Parse(text).HaltedOneSidedRootEmptyingCooldownSeconds);
    }

    [Fact]
    public void AKeyOnTheSameLineAsAnotherKeyOrTheBraceCountsAsPresent()
    {
        var text = ConfigLoader.ApplyMissingHaltedKeys(
            "{ \"HaltedOneSidedRootEmptyingAutoFix\": true, \"TrayTooltip\": \"X\", \"HaltedOneSidedRootEmptying\": \"t\" }",
            out var added);

        Assert.Equal(["HaltedOneSidedRootEmptyingCooldownSeconds"], added);
        var options = ConfigLoader.Parse(text);
        Assert.True(options.HaltedOneSidedRootEmptyingAutoFix);
        Assert.Equal("t", options.HaltedOneSidedRootEmptying);
    }

    [Fact]
    public void WorksOnAnEmptyObjectWithoutLeavingATrailingComma()
    {
        var text = ConfigLoader.ApplyMissingHaltedKeys("{}", out var added);

        Assert.Equal(AllKeys, added);
        Assert.Equal(60, ConfigLoader.Parse(text).HaltedOneSidedRootEmptyingCooldownSeconds);
    }

    [Fact]
    public void KeepsWindowsLineEndings()
    {
        var text = ConfigLoader.ApplyMissingHaltedKeys("{\r\n  \"TrayTooltip\": \"X\"\r\n}\r\n", out _);

        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void EnsureHaltedKeysRewritesTheFileOnlyWhenAKeyIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutagenmon-config-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\n  \"TrayTooltip\": \"X\"\n}");
        try
        {
            var first = ConfigLoader.EnsureHaltedKeys(path);
            var textAfterFirst = File.ReadAllText(path);
            var second = ConfigLoader.EnsureHaltedKeys(path);

            Assert.Equal(AllKeys, first);
            Assert.Empty(second);
            Assert.Equal(textAfterFirst, File.ReadAllText(path));
            Assert.Equal(60, ConfigLoader.Load(path).HaltedOneSidedRootEmptyingCooldownSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WindowSizeStillInsertsAMissingKeyAfterTheRefactoring()
    {
        var text = ConfigLoader.ApplyWindowSize("{\n  \"TrayTooltip\": \"X\"\n}", 800, 600);

        var options = ConfigLoader.Parse(text);
        Assert.Equal(800, options.StatusWindowWidth);
        Assert.Equal(600, options.StatusWindowHeight);
    }
}

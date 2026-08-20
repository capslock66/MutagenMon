using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SessionDefinitionLoaderTests
{
    [Fact]
    public void ParsesNameFromAMutagenSyncCreateLine()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "mutagen sync create --name=photos-sync -m two-way-safe C:/Photos server:/home/photos",
        });

        Assert.Single(result.Sessions);
        Assert.Equal("photos-sync", result.Sessions[0].Name);
        Assert.Empty(result.DuplicateNames);
    }

    [Fact]
    public void SkipsRemCommentedLines()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "rem mutagen sync create --name=disabled-sync -m two-way-safe C:/A B",
            "mutagen sync create --name=active-sync -m two-way-safe C:/A B",
        });

        Assert.Single(result.Sessions);
        Assert.Equal("active-sync", result.Sessions[0].Name);
    }

    [Fact]
    public void FlagsDuplicateNamesButKeepsTheLastDefinitionLikeTheLegacyApp()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "mutagen sync create --name=dup -m two-way-safe C:/First B",
            "mutagen sync create --name=dup -m two-way-safe C:/Second B",
        });

        Assert.Single(result.Sessions);
        Assert.Contains("dup", result.DuplicateNames);
        Assert.Contains("C:/Second", result.Sessions[0].RawCreateCommand);
    }

    [Fact]
    public void IgnoresLinesWithoutAName()
    {
        var result = SessionDefinitionLoader.ParseLines(new[] { "@echo off", "", "mutagen sync list" });
        Assert.Empty(result.Sessions);
    }
}

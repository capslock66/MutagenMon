using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-64.1/FR-64.2/FR-64.4: reading the `@halted-auto-fix` directive lines.</summary>
public class SessionDefinitionLoaderDirectiveTests
{
    [Fact]
    public void ReadsTheDirectivesOfActiveSessionsAndNeverTreatsThemAsSessions()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "rem sync --name=a : @halted-auto-fix: true",
            "rem sync --name=b : @halted-auto-fix: false",
            "",
            "mutagen sync terminate --all",
            "mutagen sync create --name=a X Y",
            "mutagen sync create --name=b X Y",
        });

        Assert.Equal(["a", "b"], result.Sessions.Select(s => s.Name).ToArray());
        Assert.True(result.HaltedAutoFixDirectives["a"]);
        Assert.False(result.HaltedAutoFixDirectives["b"]);
        Assert.Empty(result.DirectiveWarnings);
    }

    [Fact]
    public void AToleratesCaseAndSpacingAndAnUppercaseRem()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "REM sync --name=a :@HALTED-AUTO-FIX:TRUE",
            "mutagen sync create --name=a X Y",
        });

        Assert.Single(result.Sessions);
        Assert.True(result.HaltedAutoFixDirectives["a"]);
    }

    [Fact]
    public void AllowsTheDirectiveAnywhereInTheFile()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "mutagen sync create --name=a X Y",
            "rem sync --name=a : @halted-auto-fix: true",
        });

        Assert.True(result.HaltedAutoFixDirectives["a"]);
    }

    [Fact]
    public void TheLastDirectiveOfASessionWins()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "rem sync --name=a : @halted-auto-fix: true",
            "rem sync --name=a : @halted-auto-fix: false",
            "mutagen sync create --name=a X Y",
        });

        Assert.False(result.HaltedAutoFixDirectives["a"]);
    }

    [Fact]
    public void AnInvalidValueIsIgnoredWithAWarning()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "rem sync --name=a : @halted-auto-fix: maybe",
            "mutagen sync create --name=a X Y",
        });

        Assert.Empty(result.HaltedAutoFixDirectives);
        Assert.Contains(result.DirectiveWarnings, w => w.Contains("'a'") && w.Contains("maybe"));
    }

    [Fact]
    public void ADirectiveForAnUnknownOrDisabledSessionIsIgnoredWithAWarning()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "rem sync --name=ghost : @halted-auto-fix: true",
            "rem sync --name=off : @halted-auto-fix: true",
            "rem mutagen sync create --name=off X Y",
            "mutagen sync create --name=a X Y",
        });

        Assert.Empty(result.HaltedAutoFixDirectives);
        Assert.Equal(2, result.DirectiveWarnings.Count);
    }

    [Fact]
    public void ASessionWithoutADirectiveHasNoEntry()
    {
        var result = SessionDefinitionLoader.ParseLines(new[] { "mutagen sync create --name=a X Y" });

        Assert.False(result.HaltedAutoFixDirectives.ContainsKey("a"));
    }

    [Fact]
    public void AnOrdinaryCommentThatMentionsTheKeywordIsNotADirective()
    {
        var result = SessionDefinitionLoader.ParseLines(new[]
        {
            "rem this is about @halted-auto-fix: true for everyone",
            "mutagen sync create --name=a X Y",
        });

        Assert.Empty(result.HaltedAutoFixDirectives);
        Assert.Empty(result.DirectiveWarnings);
    }
}

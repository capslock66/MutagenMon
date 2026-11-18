using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-64.3/FR-64.5: where and how the `@halted-auto-fix` directives are written.</summary>
public class HaltedAutoFixDirectiveWriterTests
{
    private const string DirectiveA = "rem sync --name=a : @halted-auto-fix: true";
    private const string DirectiveB = "rem sync --name=b : @halted-auto-fix: false";

    [Fact]
    public void PutsTheBlockAboveTheBlankLineThatPrecedesTerminateAll()
    {
        string[] lines = ["@echo off", "rem header", "", "mutagen sync terminate --all", "mutagen sync create --name=a X Y"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        Assert.Equal(
            ["@echo off", "rem header", DirectiveA, "", "mutagen sync terminate --all", "mutagen sync create --name=a X Y"], result);
    }

    [Fact]
    public void AddsTheBlankLineWhenTerminateAllHasNoneAboveIt()
    {
        string[] lines = ["@echo off", "mutagen sync terminate --all", "mutagen sync create --name=a X Y"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        Assert.Equal(["@echo off", DirectiveA, "", "mutagen sync terminate --all", "mutagen sync create --name=a X Y"], result);
    }

    [Fact]
    public void WithoutTerminateAllTheBlockGoesBeforeTheFirstSession()
    {
        string[] lines = ["@echo off", "mutagen sync create --name=a X Y"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        Assert.Equal(["@echo off", DirectiveA, "", "mutagen sync create --name=a X Y"], result);
    }

    [Fact]
    public void WritingTheSameValueAgainChangesNothingWithoutATerminateAllLine()
    {
        string[] lines = ["@echo off", "mutagen sync create --name=a X Y"];
        var once = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        var twice = SessionEditingService.ApplyHaltedAutoFix(once, "a", "a", true);
        var thrice = SessionEditingService.ApplyHaltedAutoFix(twice, "a", "a", true);

        Assert.Equal(once, twice);
        Assert.Equal(once, thrice);
    }

    [Fact]
    public void AnotherSessionDirectiveIsAddedWithoutAnExtraBlankLineWithoutATerminateAllLine()
    {
        string[] lines = ["@echo off", "mutagen sync create --name=a X Y", "mutagen sync create --name=b X Y"];
        var withA = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        var withAAndB = SessionEditingService.ApplyHaltedAutoFix(withA, "b", "b", false);

        Assert.Equal(
            ["@echo off", DirectiveA, DirectiveB, "", "mutagen sync create --name=a X Y", "mutagen sync create --name=b X Y"], withAAndB);
    }

    [Fact]
    public void WithNeitherTheBlockGoesAtTheEndOfTheFile()
    {
        string[] lines = ["@echo off"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        Assert.Equal(["@echo off", DirectiveA], result);
    }

    [Fact]
    public void WritingTheSameValueAgainChangesNothing()
    {
        string[] lines = ["@echo off", "rem header", "", "mutagen sync terminate --all", "mutagen sync create --name=a X Y"];
        var once = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        var twice = SessionEditingService.ApplyHaltedAutoFix(once, "a", "a", true);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void ChangingTheValueRewritesTheDirectiveInPlace()
    {
        string[] lines = ["rem h", DirectiveA, DirectiveB, "", "mutagen sync terminate --all"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", false);

        Assert.Equal(["rem h", "rem sync --name=a : @halted-auto-fix: false", DirectiveB, "", "mutagen sync terminate --all"], result);
    }

    [Fact]
    public void NullRemovesTheDirectiveAndLeavesEveryOtherLine()
    {
        string[] lines = ["rem h", DirectiveA, DirectiveB, "", "mutagen sync terminate --all"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", null);

        Assert.Equal(["rem h", DirectiveB, "", "mutagen sync terminate --all"], result);
    }

    [Fact]
    public void RemovingTheLastDirectiveRemovesTheBlockButNotTheBlankLine()
    {
        string[] lines = ["rem h", DirectiveA, "", "mutagen sync terminate --all"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", null);

        Assert.Equal(["rem h", "", "mutagen sync terminate --all"], result);
    }

    [Fact]
    public void NullWithNoExistingDirectiveChangesNothing()
    {
        string[] lines = ["@echo off", "mutagen sync create --name=a X Y"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", null);

        Assert.Equal(lines, result);
    }

    [Fact]
    public void ARenameKeepsTheDirectiveAtTheSamePlaceInTheBlock()
    {
        string[] lines = ["rem h", DirectiveA, DirectiveB, "", "mutagen sync terminate --all"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "c", true);

        Assert.Equal(["rem h", "rem sync --name=c : @halted-auto-fix: true", DirectiveB, "", "mutagen sync terminate --all"], result);
    }

    [Fact]
    public void ARenameToInheritRemovesTheOldDirective()
    {
        string[] lines = ["rem h", DirectiveA, "", "mutagen sync terminate --all"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "c", null);

        Assert.Equal(["rem h", "", "mutagen sync terminate --all"], result);
    }

    [Fact]
    public void NewDirectivesAreAppendedAfterTheExistingOnes()
    {
        string[] lines = ["rem h", DirectiveA, "", "mutagen sync terminate --all"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "b", "b", false);

        Assert.Equal(["rem h", DirectiveA, DirectiveB, "", "mutagen sync terminate --all"], result);
    }

    [Fact]
    public void ScatteredAndDuplicateDirectivesAreGroupedAndCollapsed()
    {
        string[] lines =
        [
            "rem h", "", "mutagen sync terminate --all",
            "mutagen sync create --name=a X Y", "rem sync --name=a : @halted-auto-fix: false", DirectiveB, DirectiveA,
        ];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        Assert.Equal(
            ["rem h", DirectiveA, DirectiveB, "", "mutagen sync terminate --all", "mutagen sync create --name=a X Y"],
            result);
    }

    [Fact]
    public void ALineWithAnInvalidValueIsLeftWhereItIs()
    {
        string[] lines = ["rem sync --name=a : @halted-auto-fix: maybe", "mutagen sync create --name=a X Y"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        Assert.Equal(
            ["rem sync --name=a : @halted-auto-fix: maybe", DirectiveA, "", "mutagen sync create --name=a X Y"], result);
    }

    [Fact]
    public void TheTerminateAllLineIsFoundWhateverItsCase()
    {
        string[] lines = ["rem h", "", "MUTAGEN SYNC TERMINATE --ALL"];

        var result = SessionEditingService.ApplyHaltedAutoFix(lines, "a", "a", true);

        Assert.Equal(["rem h", DirectiveA, "", "MUTAGEN SYNC TERMINATE --ALL"], result);
    }
}

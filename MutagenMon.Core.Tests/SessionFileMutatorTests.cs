using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SessionFileMutatorTests
{
    [Fact]
    public void AppendAddsANewLineAtTheEndWithoutTouchingExistingLines()
    {
        string[] lines = ["@echo off", "mutagen sync create --name=a A B"];

        var result = SessionFileMutator.AppendSession(lines, "mutagen sync create --name=b C D");

        Assert.Equal(["@echo off", "mutagen sync create --name=a A B", "mutagen sync create --name=b C D"], result);
    }

    [Fact]
    public void ReplaceSwapsOnlyTheTargetSessionsLineInPlace()
    {
        string[] lines =
        [
            "@echo off",
            "rem a comment",
            "mutagen sync create --name=a A B",
            "mutagen sync create --name=b C D",
            "mutagen sync list",
        ];

        var result = SessionFileMutator.ReplaceSession(lines, "a", "mutagen sync create --name=a A2 B2 -m one-way-safe");

        Assert.Equal(
        [
            "@echo off",
            "rem a comment",
            "mutagen sync create --name=a A2 B2 -m one-way-safe",
            "mutagen sync create --name=b C D",
            "mutagen sync list",
        ], result);
    }

    [Fact]
    public void RemoveDeletesOnlyTheTargetSessionsLine()
    {
        string[] lines =
        [
            "@echo off",
            "mutagen sync create --name=a A B",
            "mutagen sync create --name=b C D",
            "mutagen sync list",
        ];

        var result = SessionFileMutator.RemoveSession(lines, "a");

        Assert.Equal(["@echo off", "mutagen sync create --name=b C D", "mutagen sync list"], result);
    }

    [Fact]
    public void ReplaceThrowsWhenTheSessionIsNotFound()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SessionFileMutator.ReplaceSession(["mutagen sync create --name=a A B"], "missing", "new line"));
    }

    [Fact]
    public void RemoveThrowsWhenTheSessionIsNotFound()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SessionFileMutator.RemoveSession(["mutagen sync create --name=a A B"], "missing"));
    }

    [Fact]
    public void ReplaceAndRemoveIgnoreRemCommentedLinesWithTheSameName()
    {
        string[] lines =
        [
            "rem mutagen sync create --name=a OLD OLD2",
            "mutagen sync create --name=a A B",
        ];

        var replaced = SessionFileMutator.ReplaceSession(lines, "a", "mutagen sync create --name=a A2 B2");
        Assert.Equal(["rem mutagen sync create --name=a OLD OLD2", "mutagen sync create --name=a A2 B2"], replaced);

        var removed = SessionFileMutator.RemoveSession(lines, "a");
        Assert.Equal(["rem mutagen sync create --name=a OLD OLD2"], removed);
    }

    [Fact]
    public void ReplaceAndRemoveTargetTheLastOccurrenceOnADuplicateNameLikeTheLoaderDoes()
    {
        string[] lines =
        [
            "mutagen sync create --name=dup FIRST F2",
            "mutagen sync create --name=dup SECOND S2",
        ];

        var replaced = SessionFileMutator.ReplaceSession(lines, "dup", "mutagen sync create --name=dup NEW N2");
        Assert.Equal(["mutagen sync create --name=dup FIRST F2", "mutagen sync create --name=dup NEW N2"], replaced);

        var removed = SessionFileMutator.RemoveSession(lines, "dup");
        Assert.Equal(["mutagen sync create --name=dup FIRST F2"], removed);
    }

    [Fact]
    public void FileWrappersRoundTripThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutagenmon-test-{Guid.NewGuid():N}.bat");
        try
        {
            File.WriteAllLines(path, ["@echo off", "mutagen sync create --name=a A B"]);

            SessionFileMutator.AppendSessionToFile(path, "mutagen sync create --name=b C D");
            Assert.Equal(
                ["@echo off", "mutagen sync create --name=a A B", "mutagen sync create --name=b C D"],
                File.ReadAllLines(path));

            SessionFileMutator.ReplaceSessionInFile(path, "b", "mutagen sync create --name=b C2 D2");
            Assert.Equal(
                ["@echo off", "mutagen sync create --name=a A B", "mutagen sync create --name=b C2 D2"],
                File.ReadAllLines(path));

            SessionFileMutator.RemoveSessionFromFile(path, "a");
            Assert.Equal(["@echo off", "mutagen sync create --name=b C2 D2"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

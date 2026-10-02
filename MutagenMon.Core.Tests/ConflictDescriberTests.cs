using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Resolution;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ConflictDescriberTests
{
    private static ConflictRecord Record(string alphaState, string betaState, string alphaName = "a.txt", string betaName = "a.txt") =>
        new(alphaName, betaName, alphaState, betaState, AutoResolved: false);

    [Fact]
    public void FileCreatedOnBothSidesIsDescribedAsDifferentContent()
    {
        var text = ConflictDescriber.Describe(Record(
            "<non-existent> -> File (95f1f6ab)", "<non-existent> -> File (37773160)"));

        Assert.Equal("Created on both sides with different content", text);
    }

    [Fact]
    public void FileModifiedOnBothSides()
    {
        var text = ConflictDescriber.Describe(Record("File (h1) -> File (h2)", "File (h1) -> File (h3)"));

        Assert.Equal("Modified on both sides (file)", text);
    }

    [Fact]
    public void ModifiedOnOneSideAndDeletedOnTheOther()
    {
        var text = ConflictDescriber.Describe(Record("File (h1) -> File (h2)", "File (h1) -> <non-existent>"));

        Assert.Equal("Alpha: modified file; Beta: deleted file", text);
    }

    [Fact]
    public void DirectoryDeletedOnOneSideAndFileAddedOnTheOther()
    {
        var text = ConflictDescriber.Describe(Record("Directory -> <non-existent>", "Directory -> File (h9)"));

        Assert.Equal("Alpha: deleted directory; Beta: directory replaced by file", text);
    }

    [Fact]
    public void ArrowInsideASymbolicLinkTargetIsNotTheSeparator()
    {
        var text = ConflictDescriber.Describe(Record("<non-existent> -> Symbolic link (a -> b)", "Directory -> <non-existent>"));

        Assert.Equal("Alpha: created symbolic link; Beta: deleted directory", text);
    }

    [Fact]
    public void UnrecognizedStateIsShownAsIs()
    {
        var text = ConflictDescriber.Describe(Record("something odd", "File (h1) -> <non-existent>"));

        Assert.Equal("Alpha: something odd; Beta: deleted file", text);
    }

    [Fact]
    public void DifferentBetaPathIsAppended()
    {
        var text = ConflictDescriber.Describe(Record(
            "<non-existent> -> File (h1)", "<non-existent> -> File (h2)", "dir/x", "dir/y"));

        Assert.Equal("Created on both sides with different content (beta path: dir/y)", text);
    }

    [Fact]
    public void DetailsKeepTheRawStates()
    {
        var details = ConflictDescriber.Details(Record("File (h1) -> File (h2)", "<non-existent> -> File (h3)"));

        Assert.Equal("alpha: File (h1) -> File (h2)\nbeta: <non-existent> -> File (h3)", details);
    }
}

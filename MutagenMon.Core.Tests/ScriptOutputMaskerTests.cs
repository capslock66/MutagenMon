using MutagenMon.Core.Scripts;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ScriptOutputMaskerTests
{
    [Fact]
    public void ReplacesEveryOccurrenceOfASecret()
    {
        var masker = new ScriptOutputMasker(["s3cret"]);

        Assert.Equal("a=*** and b=***", masker.Mask("a=s3cret and b=s3cret"));
    }

    [Fact]
    public void MasksSeveralSecretsInOneLine()
    {
        var masker = new ScriptOutputMasker(["one", "two"]);

        Assert.Equal("*** *** three", masker.Mask("one two three"));
    }

    [Fact]
    public void MasksALongSecretAsAWholeEvenWhenAShorterOneIsContainedInIt()
    {
        var masker = new ScriptOutputMasker(["pass", "mypassword"]);

        Assert.Equal("***", masker.Mask("mypassword"));
    }

    [Fact]
    public void TheMatchIsExactAndCaseSensitive()
    {
        var masker = new ScriptOutputMasker(["Secret"]);

        Assert.Equal("secret ***", masker.Mask("secret Secret"));
    }

    [Fact]
    public void ATransformedSecretIsNotMasked()
    {
        var masker = new ScriptOutputMasker(["s3cret"]);

        Assert.Equal("czNjcmV0", masker.Mask("czNjcmV0"));
    }

    [Fact]
    public void EmptySecretsAreIgnored()
    {
        var masker = new ScriptOutputMasker(["", "x"]);

        Assert.Equal("a***b", masker.Mask("axb"));
        Assert.Equal("unchanged", new ScriptOutputMasker([""]).Mask("unchanged"));
    }

    [Fact]
    public void WithoutSecretsALineIsReturnedAsIs()
    {
        var masker = new ScriptOutputMasker([]);

        Assert.Equal("hello", masker.Mask("hello"));
    }
}

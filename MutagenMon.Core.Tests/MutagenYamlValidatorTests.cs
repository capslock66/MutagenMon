using MutagenMon.Core.Configuration;
using Xunit;

namespace MutagenMon.Core.Tests;

public class MutagenYamlValidatorTests
{
    [Fact]
    public void ValidYamlReturnsNoErrors()
    {
        var errors = MutagenYamlValidator.Validate("synchronization:\n  defaults:\n    ignoreVCSDirectories: true\n");

        Assert.Empty(errors);
    }

    [Fact]
    public void EmptyTextIsValid()
    {
        var errors = MutagenYamlValidator.Validate("");

        Assert.Empty(errors);
    }

    [Fact]
    public void MalformedYamlReturnsAtLeastOneErrorMessage()
    {
        var errors = MutagenYamlValidator.Validate("a: [1, 2\nb: unterminated \"quote");

        Assert.NotEmpty(errors);
    }
}

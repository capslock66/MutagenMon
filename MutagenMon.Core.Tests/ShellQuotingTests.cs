using MutagenMon.Core.Ssh;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ShellQuotingTests
{
    [Theory]
    [InlineData("plain.txt", "'plain.txt'")]
    [InlineData("it's.txt", "'it'\\''s.txt'")]
    [InlineData("a'; rm -rf ~; echo '.txt", "'a'\\''; rm -rf ~; echo '\\''.txt'")]
    [InlineData("$(reboot).txt", "'$(reboot).txt'")]
    [InlineData("`id`.txt", "'`id`.txt'")]
    public void QuoteKeepsHostileValuesAsOneLiteralWord(string value, string expected)
    {
        Assert.Equal(expected, ShellQuoting.Quote(value));
    }
}

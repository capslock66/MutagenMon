using MutagenMon.Core.Ssh;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SshConfigParserTests
{
    [Fact]
    public void ParsesHostNameUserPortAndIdentityFileFromAMatchingBlock()
    {
        var entry = SshConfigParser.Parse(new[]
        {
            "Host robbie",
            "    HostName 192.168.1.42",
            "    User deploy",
            "    Port 2222",
            "    IdentityFile ~/.ssh/robbie_key",
        }, "robbie");

        Assert.Equal("192.168.1.42", entry.HostName);
        Assert.Equal("deploy", entry.User);
        Assert.Equal(2222, entry.Port);
        Assert.Equal("~/.ssh/robbie_key", entry.IdentityFile);
    }

    [Fact]
    public void ReturnsAllNullsWhenNoBlockMatchesTheHost()
    {
        var entry = SshConfigParser.Parse(new[] { "Host other", "    HostName 10.0.0.1" }, "robbie");

        Assert.Null(entry.HostName);
        Assert.Null(entry.User);
        Assert.Null(entry.Port);
        Assert.Null(entry.IdentityFile);
    }

    [Fact]
    public void IgnoresKeysOutsideAnyMatchingBlock()
    {
        var entry = SshConfigParser.Parse(new[]
        {
            "HostName should-be-ignored",
            "Host robbie",
            "    HostName 192.168.1.42",
        }, "robbie");

        Assert.Equal("192.168.1.42", entry.HostName);
    }

    [Fact]
    public void StopsApplyingAPreviousBlocksKeysOnceANewHostLineIsSeen()
    {
        var entry = SshConfigParser.Parse(new[]
        {
            "Host robbie",
            "    User deploy",
            "Host other",
            "    HostName 10.0.0.1",
        }, "robbie");

        Assert.Equal("deploy", entry.User);
        Assert.Null(entry.HostName);
    }

    [Fact]
    public void MatchesOneOfSeveralSpaceSeparatedAliasesOnAHostLine()
    {
        var entry = SshConfigParser.Parse(new[] { "Host robbie other-alias", "    User deploy" }, "other-alias");

        Assert.Equal("deploy", entry.User);
    }

    [Fact]
    public void IgnoresCommentAndBlankLines()
    {
        var entry = SshConfigParser.Parse(new[]
        {
            "# a comment",
            "",
            "Host robbie",
            "    # another comment",
            "    User deploy",
        }, "robbie");

        Assert.Equal("deploy", entry.User);
    }

    [Fact]
    public void AcceptsKeyEqualsValueSyntax()
    {
        var entry = SshConfigParser.Parse(new[] { "Host robbie", "    Port=2222" }, "robbie");

        Assert.Equal(2222, entry.Port);
    }

    [Fact]
    public void HostMatchingIsCaseInsensitive()
    {
        var entry = SshConfigParser.Parse(new[] { "Host Robbie", "    User deploy" }, "robbie");

        Assert.Equal("deploy", entry.User);
    }
}

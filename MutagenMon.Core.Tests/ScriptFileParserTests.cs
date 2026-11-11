using MutagenMon.Core.Scripts;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ScriptFileParserTests
{
    private static ScriptDefinition ParseSingle(params string[] lines)
    {
        var scripts = ScriptFileParser.ParseLines(lines);
        return Assert.Single(scripts);
    }

    [Fact]
    public void ParsesSectionsInFileOrder()
    {
        var scripts = ScriptFileParser.ParseLines(new[]
        {
            "[first]", "# @host: h1", "echo 1",
            "[second]", "# @host: h2", "echo 2",
        });

        Assert.Equal(new[] { "first", "second" }, scripts.Select(s => s.Name));
        Assert.Equal("# @host: h1\necho 1", scripts[0].Body);
        Assert.Equal("# @host: h2\necho 2", scripts[1].Body);
    }

    [Fact]
    public void ReturnsNothingForAnEmptyFile()
    {
        Assert.Empty(ScriptFileParser.ParseLines(Array.Empty<string>()));
    }

    [Fact]
    public void IgnoresLinesBeforeTheFirstHeader()
    {
        var script = ParseSingle("# some comment", "echo ignored", "[only]", "# @host: h", "echo kept");

        Assert.Equal("only", script.Name);
        Assert.DoesNotContain("ignored", script.Body);
        Assert.DoesNotContain("comment", script.Body);
    }

    [Fact]
    public void BashTestsInABodyAreNotSectionHeaders()
    {
        var script = ParseSingle(
            "[s]", "# @host: h",
            "[ -f x ] && echo yes",
            "[ ok ]",
            "[[ -n \"$x\" ]]",
            "[[ -z $y ]] || echo no");

        Assert.Contains("[ -f x ] && echo yes", script.Body);
        Assert.Contains("[ ok ]", script.Body);
        Assert.Contains("[[ -n \"$x\" ]]", script.Body);
        Assert.Contains("[[ -z $y ]] || echo no", script.Body);
    }

    [Fact]
    public void ALineWithASpaceRightAfterTheBracketIsNotAHeader()
    {
        var script = ParseSingle("[s]", "# @host: h", "[ name]");

        Assert.Contains("[ name]", script.Body);
    }

    [Fact]
    public void HeaderNamesMayContainSpacesDotsDashesAndUnderscores()
    {
        var script = ParseSingle("  [my script.v-2_x]  ", "# @host: h", "echo");

        Assert.Equal("my script.v-2_x", script.Name);
    }

    [Fact]
    public void TrimsLeadingAndTrailingBlankLinesOfABodyOnly()
    {
        var script = ParseSingle("[s]", "", "   ", "# @host: h", "", "echo a", "", "");

        Assert.Equal("# @host: h\n\necho a", script.Body);
    }

    [Fact]
    public void KeepsDirectiveAndCommentLinesInTheBody()
    {
        var script = ParseSingle("[s]", "# @host: h", "# @description: d", "# plain comment", "echo");

        Assert.Equal("# @host: h\n# @description: d\n# plain comment\necho", script.Body);
    }

    [Fact]
    public void ReadsAllDirectives()
    {
        var script = ParseSingle(
            "[s]", "# @host: robbie", "# @description: Start things", "# @stop-on-error: false", "# @confirm: true", "echo");

        Assert.True(script.IsValid);
        Assert.Equal("robbie", script.Host);
        Assert.Equal("Start things", script.Description);
        Assert.False(script.StopOnError);
        Assert.True(script.Confirm);
    }

    [Fact]
    public void ReadsTheGroupDirective()
    {
        var script = ParseSingle("[s]", "# @host: robbie", "# @group:  My group ", "echo");

        Assert.Equal("My group", script.Group);
    }

    [Fact]
    public void ScriptsHaveNoGroupByDefault()
    {
        Assert.Equal("", ParseSingle("[s]", "# @host: robbie", "echo").Group);
    }

    [Fact]
    public void GroupNamesAreCaseSensitive()
    {
        var scripts = ScriptFileParser.ParseLines(
            new[] { "[a]", "# @host: h", "# @group: Tools", "[b]", "# @host: h", "# @group: tools" });

        Assert.NotEqual(scripts[0].Group, scripts[1].Group);
    }

    [Fact]
    public void ReadGroupTakesTheFirstOccurrence()
    {
        Assert.Equal("one", ScriptFileParser.ReadGroup(new[] { "[s]", "# @group: one", "# @group: two" }));
        Assert.Equal("", ScriptFileParser.ReadGroup(new[] { "[s]", "echo" }));
    }

    [Fact]
    public void AppliesDefaultsWhenOptionalDirectivesAreAbsent()
    {
        var script = ParseSingle("[s]", "# @host: robbie", "echo");

        Assert.Equal("", script.Description);
        Assert.True(script.StopOnError);
        Assert.False(script.Confirm);
    }

    [Fact]
    public void IgnoresUnknownDirectives()
    {
        var script = ParseSingle("[s]", "# @host: h", "# @colour: red", "echo");

        Assert.True(script.IsValid);
    }

    [Fact]
    public void FirstOccurrenceOfADirectiveWins()
    {
        var script = ParseSingle("[s]", "# @host: first", "# @host: second", "echo");

        Assert.Equal("first", script.Host);
    }

    [Fact]
    public void DirectiveKeysAndBooleansAreCaseInsensitive()
    {
        var script = ParseSingle("[s]", "# @HOST: h", "# @Confirm: TRUE", "echo");

        Assert.Equal("h", script.Host);
        Assert.True(script.Confirm);
    }

    [Fact]
    public void AScriptWithoutAHostIsListedButInvalid()
    {
        var script = ParseSingle("[s]", "echo");

        Assert.False(script.IsValid);
        Assert.Null(script.Host);
        Assert.Contains("@host", script.ValidationError);
    }

    [Fact]
    public void AnEmptyHostIsInvalid()
    {
        var script = ParseSingle("[s]", "# @host:", "echo");

        Assert.False(script.IsValid);
    }

    [Fact]
    public void ANonBooleanValueMakesTheScriptInvalid()
    {
        var script = ParseSingle("[s]", "# @host: h", "# @confirm: ture", "echo");

        Assert.False(script.IsValid);
        Assert.Contains("@confirm", script.ValidationError);
    }

    [Fact]
    public void DuplicateNamesAreAllMarkedInvalid()
    {
        var scripts = ScriptFileParser.ParseLines(new[]
        {
            "[dup]", "# @host: h", "echo 1",
            "[ok]", "# @host: h", "echo 2",
            "[dup]", "# @host: h", "echo 3",
        });

        Assert.Equal(3, scripts.Count);
        Assert.False(scripts[0].IsValid);
        Assert.True(scripts[1].IsValid);
        Assert.False(scripts[2].IsValid);
        Assert.Contains("duplicate", scripts[0].ValidationError);
    }

    [Fact]
    public void StripsCarriageReturnsFromLines()
    {
        var script = ParseSingle("[s]\r", "# @host: h\r", "echo a\r", "echo b\r");

        Assert.Equal("s", script.Name);
        Assert.Equal("# @host: h\necho a\necho b", script.Body);
        Assert.DoesNotContain('\r', script.Body);
    }

    [Fact]
    public void ParseFileAcceptsCrLfAndLfFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N") + ".ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[a]\r\n# @host: h\r\necho a\r\n\n[b]\n# @host: h\necho b\n");

        var scripts = ScriptFileParser.ParseFile(path);

        Assert.Equal(new[] { "a", "b" }, scripts.Select(s => s.Name));
        Assert.Equal("# @host: h\necho a", scripts[0].Body);
        Assert.Equal("# @host: h\necho b", scripts[1].Body);
    }

    [Fact]
    public void ParseFileThrowsForAMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N") + ".ini");

        Assert.Throws<FileNotFoundException>(() => ScriptFileParser.ParseFile(path));
    }

    // ---- @scriptSession flag (FR-48.5, FR-56) ----

    [Theory]
    [InlineData("# @scriptSession")]
    [InlineData("# @scriptSession: true")]
    [InlineData("# @scriptSession:true")]
    [InlineData("# @scriptSession:")]
    [InlineData("#@scriptSession")]
    [InlineData("# @scriptsession")]
    [InlineData("# @SCRIPTSESSION: TRUE")]
    [InlineData("   # @scriptSession   ")]
    public void ScriptSessionFlagIsTrueWhenPresent(string line)
    {
        var script = ParseSingle("[s]", "# @host: h", line, "echo");

        Assert.True(script.IsSessionScript);
        Assert.True(script.IsValid);
    }

    [Fact]
    public void ScriptSessionFlagIsFalseWhenAbsent()
    {
        Assert.False(ParseSingle("[s]", "# @host: h", "echo").IsSessionScript);
    }

    [Theory]
    [InlineData("# @scriptSession: false")]
    [InlineData("# @scriptSession : FALSE")]
    public void ScriptSessionFlagCanBeExplicitlyFalse(string line)
    {
        var script = ParseSingle("[s]", "# @host: h", line, "echo");

        Assert.False(script.IsSessionScript);
        Assert.True(script.IsValid);
    }

    [Fact]
    public void ScriptSessionFlagWithAnotherValueMakesTheScriptInvalid()
    {
        var script = ParseSingle("[s]", "# @host: h", "# @scriptSession: maybe", "echo");

        Assert.False(script.IsValid);
        Assert.Contains("@scriptSession", script.ValidationError);
        Assert.False(script.IsSessionScript);
    }

    [Theory]
    [InlineData("# @scriptSessionX")]
    [InlineData("# @scriptSessions")]
    [InlineData("# @scriptSession extra words")]
    [InlineData("echo @scriptSession")]
    [InlineData("@scriptSession")]
    public void ScriptSessionFlagIsNotRecognisedInOtherLines(string line)
    {
        var script = ParseSingle("[s]", "# @host: h", line, "echo");

        Assert.False(script.IsSessionScript);
        Assert.True(script.IsValid);
    }

    [Fact]
    public void ScriptSessionFlagTakesTheFirstOccurrence()
    {
        Assert.False(ParseSingle("[s]", "# @host: h", "# @scriptSession: false", "# @scriptSession", "echo").IsSessionScript);
        Assert.True(ParseSingle("[s]", "# @host: h", "# @scriptSession", "# @scriptSession: false", "echo").IsSessionScript);
        // A later invalid duplicate is ignored, like the other directives.
        Assert.True(ParseSingle("[s]", "# @host: h", "# @scriptSession", "# @scriptSession: maybe", "echo").IsValid);
    }

    [Fact]
    public void ScriptSessionFlagCanFollowNonDirectiveLines()
    {
        var script = ParseSingle("[s]", "# @host: h", "echo first", "# plain comment", "# @scriptSession", "echo last");

        Assert.True(script.IsSessionScript);
    }

    [Fact]
    public void ScriptSessionFlagIsKeptOnAnOtherwiseInvalidScript()
    {
        var script = ParseSingle("[s]", "# @scriptSession", "echo");

        Assert.False(script.IsValid);
        Assert.Contains("missing @host", script.ValidationError);
        Assert.True(script.IsSessionScript);
    }

    [Fact]
    public void ScriptSessionFlagStaysInTheBody()
    {
        var script = ParseSingle("[s]", "# @host: h", "# @scriptSession", "echo");

        Assert.Equal("# @host: h\n# @scriptSession\necho", script.Body);
    }

    [Fact]
    public void ScriptSessionFlagIsPerScript()
    {
        var scripts = ScriptFileParser.ParseLines(new[]
        {
            "[a]", "# @host: h", "# @scriptSession", "echo a",
            "[b]", "# @host: h", "echo b",
        });

        Assert.Equal(new[] { true, false }, scripts.Select(s => s.IsSessionScript));
    }

    [Theory]
    [InlineData("name", true)]
    [InlineData("my script.v-2_x", true)]
    [InlineData("9lives", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("[nested]", false)]
    [InlineData("a]b", false)]
    [InlineData("a/b", false)]
    public void IsValidNameFollowsTheHeaderFormat(string name, bool expected)
    {
        Assert.Equal(expected, ScriptFileParser.IsValidName(name));
    }
}

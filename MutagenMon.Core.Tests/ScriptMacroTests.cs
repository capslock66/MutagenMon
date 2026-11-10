using MutagenMon.Core.Scripts;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ScriptMacroTests
{
    [Fact]
    public void FindsOneMacroWithItsSpanTypeAndName()
    {
        const string text = "echo {{env:DB_PWD}} done";

        var macro = Assert.Single(ScriptMacro.Find(text));

        Assert.Equal(ScriptMacroType.Env, macro.Type);
        Assert.Equal("DB_PWD", macro.Name);
        Assert.False(macro.IsQuoted);
        Assert.Equal("{{env:DB_PWD}}", text.Substring(macro.Start, macro.Length));
    }

    [Fact]
    public void FindsSeveralMacrosOnOneLineInOrder()
    {
        var macros = ScriptMacro.Find("{{session:name}}-{{env:SUFFIX}}");

        Assert.Equal(new[] { "name", "SUFFIX" }, macros.Select(macro_ => macro_.Name));
        Assert.Equal(new[] { ScriptMacroType.Session, ScriptMacroType.Env }, macros.Select(macro_ => macro_.Type));
    }

    [Fact]
    public void FindsAdjacentMacrosWithoutSeparator()
    {
        var macros = ScriptMacro.Find("{{env:A}}{{env:B}}");

        Assert.Equal(new[] { "A", "B" }, macros.Select(macro_ => macro_.Name));
        Assert.Equal(new[] { 0, 9 }, macros.Select(macro_ => macro_.Start));
    }

    [Theory]
    [InlineData("{{env:NAME|quote}}", ScriptMacroType.Env, "NAME")]
    [InlineData("{{session:name|quote}}", ScriptMacroType.Session, "name")]
    [InlineData("{{reg:HKCU\\Software\\MyApp:DbPassword|quote}}", ScriptMacroType.Reg, "HKCU\\Software\\MyApp:DbPassword")]
    [InlineData("{{cred:MyApp/DbPassword|quote}}", ScriptMacroType.Cred, "MyApp/DbPassword")]
    [InlineData("{{secret:Database password|quote}}", ScriptMacroType.Secret, "Database password")]
    public void RecognisesTheQuoteModifierOnEveryType(string text, ScriptMacroType expectedType, string expectedName)
    {
        var macro = Assert.Single(ScriptMacro.Find(text));

        Assert.Equal(expectedType, macro.Type);
        Assert.Equal(expectedName, macro.Name);
        Assert.True(macro.IsQuoted);
        Assert.Equal(text.Length, macro.Length);
    }

    [Fact]
    public void KeepsColonsAndSlashesInACredentialTarget()
    {
        var macro = Assert.Single(ScriptMacro.Find(@"{{cred:Domain\app:prod/db}}"));

        Assert.Equal(@"Domain\app:prod/db", macro.Name);
    }

    [Fact]
    public void ReportsAnUnknownTypeInsteadOfIgnoringIt()
    {
        var macro = Assert.Single(ScriptMacro.Find("{{foo:bar}}"));

        Assert.Equal(ScriptMacroType.Unknown, macro.Type);
        Assert.Equal("foo", macro.TypeName);
    }

    [Fact]
    public void TheTypeIsCaseSensitive()
    {
        var macro = Assert.Single(ScriptMacro.Find("{{ENV:NAME}}"));

        Assert.Equal(ScriptMacroType.Unknown, macro.Type);
    }

    [Theory]
    [InlineData("echo ${VAR} and $(cmd) and `id`")]
    [InlineData("text with {{ but no closing")]
    [InlineData("{{notamacro}}")]
    [InlineData("{{ env:NAME }}")]
    [InlineData("{{1:name}}")]
    [InlineData("[[ -n \"$x\" ]] && echo {single}")]
    public void TextThatMerelyContainsBracesIsNotAMacro(string text)
    {
        Assert.Empty(ScriptMacro.Find(text));
    }

    [Theory]
    [InlineData("{{env:NA\nME}}")]
    [InlineData("{{env:NAME\r\n}}")]
    [InlineData("{{env:NAME\n}}")]
    public void ATokenSplitAcrossLinesIsNotMatched(string text)
    {
        Assert.Empty(ScriptMacro.Find(text));
    }

    [Fact]
    public void AnUnclosedTokenCannotSwallowTheMacroThatFollowsIt()
    {
        var unclosedThenMacro = Assert.Single(ScriptMacro.Find("echo \"{{env:X\" ; echo {{env:Y}}"));
        var brokenTypeThenMacro = Assert.Single(ScriptMacro.Find("{{a:b {{env:X}}"));

        Assert.Equal("Y", unclosedThenMacro.Name);
        Assert.Equal(ScriptMacroType.Env, brokenTypeThenMacro.Type);
        Assert.Equal("X", brokenTypeThenMacro.Name);
    }

    [Fact]
    public void AnEmptyNameIsFoundSoTheExpanderCanReportIt()
    {
        var macro = Assert.Single(ScriptMacro.Find("{{env:}}"));

        Assert.Equal("", macro.Name);
    }

    [Fact]
    public void DisplayShowsTheSourceFormWithoutAnyValue()
    {
        Assert.Equal("{{env:DB_PWD}}", Assert.Single(ScriptMacro.Find("{{env:DB_PWD}}")).Display);
        Assert.Equal("{{secret:Token|quote}}", Assert.Single(ScriptMacro.Find("{{secret:Token|quote}}")).Display);
    }

    [Fact]
    public void SecretLabelsListEachLabelOnceInOrderOfFirstAppearance()
    {
        var macros = ScriptMacro.Find(
            "{{secret:Database password}} {{env:X}} {{secret:API token}} {{secret:Database password|quote}}");

        Assert.Equal(new[] { "Database password", "API token" }, ScriptMacro.SecretLabels(macros));
    }

    [Fact]
    public void SecretLabelsAreCaseSensitiveAndSkipEmptyLabels()
    {
        var macros = ScriptMacro.Find("{{secret:Token}} {{secret:token}} {{secret:}}");

        Assert.Equal(new[] { "Token", "token" }, ScriptMacro.SecretLabels(macros));
    }

    [Fact]
    public void ScriptWithoutMacroHasNoSecretLabel()
    {
        Assert.Empty(ScriptMacro.SecretLabels(ScriptMacro.Find("echo hello")));
    }

    // Comments (FR-55.1): everything to the right of a bash comment marker is ignored.

    [Theory]
    [InlineData("#echo \"reg port: [{{reg:HKCU\\Software\\MyAppTest:Blob}}\"")]
    [InlineData("   # {{env:A}}")]
    [InlineData("echo ok # {{env:A}}")]
    [InlineData("echo ok\t#{{env:A}}")]
    [InlineData("echo ok;# {{env:A}}")]
    [InlineData("(echo ok)# {{env:A}}")]
    [InlineData("echo \"x\" # \"{{env:A}}\"")]
    [InlineData("# {{secret:A}} {{env:B}}")]
    [InlineData("echo >#{{env:A}}")]
    [InlineData("echo a \\\n# {{env:A}}")]
    public void AMacroToTheRightOfACommentMarkerIsIgnored(string text)
    {
        Assert.Empty(ScriptMacro.Find(text));
    }

    [Fact]
    public void OnlyTheMacrosBeforeTheCommentMarkerAreFound()
    {
        var macros = ScriptMacro.Find("echo {{env:A}} {{env:B}} # {{env:C}} {{env:D}}");

        Assert.Equal(new[] { "A", "B" }, macros.Select(macro_ => macro_.Name));
    }

    [Fact]
    public void ACommentEndsAtTheEndOfItsLine()
    {
        var macros = ScriptMacro.Find("# {{env:A}}\necho {{env:B}}\r\n  # {{env:C}}\r\necho {{env:D}}");

        Assert.Equal(new[] { "B", "D" }, macros.Select(macro_ => macro_.Name));
    }

    [Theory]
    [InlineData("echo \"a # {{env:A}}\"")]
    [InlineData("echo 'a # {{env:A}}'")]
    [InlineData("echo a#b {{env:A}}")]
    [InlineData("echo $# {{env:A}}")]
    [InlineData("echo ${#VAR} {{env:A}}")]
    [InlineData("echo \\# {{env:A}}")]
    [InlineData("echo \"it\\\"s # {{env:A}}\"")]
    [InlineData("echo \"a # b\" {{env:A}}")]
    [InlineData("echo a #{{env:A}}")]
    [InlineData("echo \\\\# {{env:A}}")]
    [InlineData("echo \"a \\\n# {{env:A}}\"")]
    public void AHashThatIsNotACommentMarkerDoesNotHideAMacro(string text)
    {
        var macro = Assert.Single(ScriptMacro.Find(text));

        Assert.Equal("A", macro.Name);
    }

    [Fact]
    public void AQuoteOrAHashInAMacroNameDoesNotChangeWhereTheCommentStarts()
    {
        var macros = ScriptMacro.Find("echo {{secret:Bob's password}} {{secret:Item #2}} {{env:A}} # {{env:B}}");

        Assert.Equal(new[] { "Bob's password", "Item #2", "A" }, macros.Select(macro_ => macro_.Name));
    }

    // Documented limits of the line by line scan (FR-55.1): they never let a value go out unmasked.
    [Fact]
    public void AQuoteClosedOnALaterLineDoesNotHideTheMacrosOfARealComment()
    {
        var macro = Assert.Single(ScriptMacro.Find("echo 'a\nb' # {{env:A}}"));

        Assert.Equal("A", macro.Name);
    }

    [Fact]
    public void AnAnsiCQuoteWithAnEscapedQuoteIsNotRecognised()
    {
        Assert.Empty(ScriptMacro.Find("echo $'a\\'b # {{env:A}}'"));
    }

    [Fact]
    public void AnOpenQuoteDoesNotCarryOverToTheNextLine()
    {
        var macros = ScriptMacro.Find("echo \"open\n# {{env:A}}\necho {{env:B}}");

        Assert.Equal(new[] { "B" }, macros.Select(macro_ => macro_.Name));
    }

    [Fact]
    public void ASecretInACommentIsNotListedAsASecretLabel()
    {
        var macros = ScriptMacro.Find("# {{secret:Old}}\necho {{secret:New}} # {{secret:Other}}");

        Assert.Equal(new[] { "New" }, ScriptMacro.SecretLabels(macros));
    }
}

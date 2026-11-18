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
    public void ValueRequestsListEachSecretLabelOnceInOrderOfFirstAppearance()
    {
        var macros = ScriptMacro.Find(
            "{{secret:Database password}} {{env:X}} {{secret:API token}} {{secret:Database password|quote}}");

        var requests = ScriptMacro.ValueRequests(macros);

        Assert.Equal(new[] { "Database password", "API token" }, requests.Select(request_ => request_.Label));
        Assert.All(requests, request_ => Assert.True(request_.IsSecret));
    }

    [Fact]
    public void ValueRequestsAreCaseSensitiveAndSkipEmptyLabels()
    {
        var macros = ScriptMacro.Find("{{secret:Token}} {{secret:token}} {{secret:}}");

        var requests = ScriptMacro.ValueRequests(macros);

        Assert.Equal(new[] { "Token", "token" }, requests.Select(request_ => request_.Label));
        Assert.All(requests, request_ => Assert.True(request_.IsSecret));
    }

    [Fact]
    public void ScriptWithoutMacroHasNoValueRequest()
    {
        Assert.Empty(ScriptMacro.ValueRequests(ScriptMacro.Find("echo hello")));
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

        var request = Assert.Single(ScriptMacro.ValueRequests(macros));

        Assert.Equal("New", request.Label);
        Assert.True(request.IsSecret);
    }

    // Prompt macro and its default modifier (FR-55.1, FR-55.12).

    [Fact]
    public void ThePromptMacroIsOfTypePrompt()
    {
        var plain = Assert.Single(ScriptMacro.Find("{{prompt:Label}}"));
        var quoted = Assert.Single(ScriptMacro.Find("{{prompt:Label|quote}}"));

        Assert.Equal(ScriptMacroType.Prompt, plain.Type);
        Assert.Equal("prompt", plain.TypeName);
        Assert.Equal("Label", plain.Name);
        Assert.False(plain.IsQuoted);
        Assert.Null(plain.DefaultText);
        Assert.Null(plain.Default);
        Assert.Equal(ScriptMacroType.Prompt, quoted.Type);
        Assert.Equal("Label", quoted.Name);
        Assert.True(quoted.IsQuoted);
    }

    [Theory]
    [InlineData("{{prompt:Label|default=/config}}", false)]
    [InlineData("{{prompt:Label|default=/config|quote}}", true)]
    [InlineData("{{prompt:Label|quote|default=/config}}", true)]
    public void ThePromptDefaultModifierIsReadInEitherOrderWithQuote(string text, bool expectedQuoted)
    {
        var macro = Assert.Single(ScriptMacro.Find(text));

        Assert.Equal(ScriptMacroType.Prompt, macro.Type);
        Assert.Equal("Label", macro.Name);
        Assert.Equal("/config", macro.DefaultText);
        Assert.Equal("/config", macro.Default);
        Assert.Equal(expectedQuoted, macro.IsQuoted);
        Assert.Equal(text.Length, macro.Length);
    }

    [Fact]
    public void AnEmptyPromptDefaultIsEmptyTextAndNoDefault()
    {
        var macro = Assert.Single(ScriptMacro.Find("{{prompt:Label|default=}}"));

        Assert.Equal("Label", macro.Name);
        Assert.Equal("", macro.DefaultText);
        Assert.Null(macro.Default);
    }

    [Theory]
    [InlineData("a:b=c d")]
    [InlineData("k=v")]
    [InlineData("  padded  ")]
    [InlineData("a}b")]
    public void APromptDefaultMayContainColonsEqualsSpacesAndASingleClosingBrace(string defaultText)
    {
        var macro = Assert.Single(ScriptMacro.Find("{{prompt:Label|default=" + defaultText + "}}"));

        Assert.Equal("Label", macro.Name);
        Assert.Equal(defaultText, macro.DefaultText);
    }

    [Fact]
    public void APromptLabelMayContainEqualsAndColons()
    {
        var macro = Assert.Single(ScriptMacro.Find("{{prompt:k=v:x|default=a}}"));

        Assert.Equal("k=v:x", macro.Name);
        Assert.Equal("a", macro.DefaultText);
    }

    [Fact]
    public void APromptMacroInACommentIsIgnored()
    {
        var macros = ScriptMacro.Find("# {{prompt:Old|default=x}}\necho {{prompt:New}} # {{prompt:Other}}");

        var macro = Assert.Single(macros);
        Assert.Equal("New", macro.Name);
        Assert.Equal("New", Assert.Single(ScriptMacro.ValueRequests(macros)).Label);
    }

    [Fact]
    public void DisplayShowsTheDefaultTextOfAPromptMacro()
    {
        Assert.Equal("{{prompt:Label}}", Assert.Single(ScriptMacro.Find("{{prompt:Label}}")).Display);
        Assert.Contains("/config", Assert.Single(ScriptMacro.Find("{{prompt:Label|default=/config}}")).Display);
        Assert.Contains("/config", Assert.Single(ScriptMacro.Find("{{prompt:Label|quote|default=/config}}")).Display);
    }

    [Fact]
    public void ValueRequestsMixSecretAndPromptLabelsInOrderOfFirstAppearance()
    {
        var macros = ScriptMacro.Find("{{prompt:Path}} {{secret:Token}} {{env:X}} {{prompt:Path|quote}} {{secret:Other}} {{prompt:Mode}}");

        var requests = ScriptMacro.ValueRequests(macros);

        Assert.Equal(new[] { "Path", "Token", "Other", "Mode" }, requests.Select(request_ => request_.Label));
        Assert.Equal(new[] { false, true, true, false }, requests.Select(request_ => request_.IsSecret));
    }

    [Fact]
    public void ValueRequestsAreCaseSensitiveAndSkipEmptyPromptLabels()
    {
        var macros = ScriptMacro.Find("{{prompt:Path}} {{prompt:path}} {{prompt:}}");

        Assert.Equal(new[] { "Path", "path" }, ScriptMacro.ValueRequests(macros).Select(request_ => request_.Label));
    }

    [Fact]
    public void ValueRequestsCarryTheDefaultOfThePromptLabel()
    {
        var macros = ScriptMacro.Find("{{prompt:Path|default=/config}} {{prompt:Name}}");

        var requests = ScriptMacro.ValueRequests(macros);

        Assert.Equal(new string?[] { "/config", null }, requests.Select(request_ => request_.Default));
    }

    [Fact]
    public void ValueRequestsCarryTheDefaultWhenOnlyOneMacroOfTheLabelHasIt()
    {
        var first = ScriptMacro.ValueRequests(ScriptMacro.Find("{{prompt:Path}} {{prompt:Path|default=/config}}"));
        var last = ScriptMacro.ValueRequests(ScriptMacro.Find("{{prompt:Path|default=/config}} {{prompt:Path|quote}}"));

        Assert.Equal("/config", Assert.Single(first).Default);
        Assert.Equal("/config", Assert.Single(last).Default);
    }

    [Fact]
    public void ValueRequestsHaveNoDefaultForAnEmptyDefaultAndForASecretRow()
    {
        var requests = ScriptMacro.ValueRequests(ScriptMacro.Find("{{prompt:Path|default=}} {{secret:Token}}"));

        Assert.All(requests, request_ => Assert.Null(request_.Default));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public void ValueRequestsIgnoreTheRejectedMacros()
    {
        // Rejected: a default left in a secret label, a repeated quote on a prompt, a misplaced quote on a prompt, a default on a secret.
        var requests = ScriptMacro.ValueRequests(ScriptMacro.Find(
            "{{secret:a|default=x|b}} {{prompt:c|quote|quote}} {{prompt:d|quote|e}} {{secret:pw|default=x}} {{secret:Ok}}"));

        var request = Assert.Single(requests);
        Assert.Equal("Ok", request.Label);
    }

    [Fact]
    public void ALabelUsedByASecretAndAPromptMacroIsListedOnceWithTheTypeOfItsFirstAppearance()
    {
        var requests = ScriptMacro.ValueRequests(ScriptMacro.Find("{{secret:L}} {{prompt:L}}"));

        var request = Assert.Single(requests);
        Assert.Equal("L", request.Label);
        Assert.True(request.IsSecret);
    }

    [Theory]
    [InlineData("{{env:A|required}}", "A", false, true, null)]
    [InlineData("{{env:A|quote|required}}", "A", true, true, null)]
    [InlineData("{{env:A|required|quote}}", "A", true, true, null)]
    [InlineData("{{prompt:Cmd|default=/config|required}}", "Cmd", false, true, "/config")]
    [InlineData("{{prompt:Cmd|required|default=/config|quote}}", "Cmd", true, true, "/config")]
    [InlineData("{{prompt:Cmd|quote|default=/config}}", "Cmd", true, false, "/config")]
    [InlineData("{{secret:S}}", "S", false, false, null)]
    [InlineData("{{secret:S|requiredx}}", "S|requiredx", false, false, null)]
    [InlineData("{{env:X|quote|quote|required}}", "X|quote", true, true, null)]
    public void TheRequiredModifierIsAFlagInAnyOrderWithTheOtherModifiers(string text, string name, bool isQuoted, bool isRequired, string? defaultText)
    {
        var macro = Assert.Single(ScriptMacro.Find(text));

        Assert.Equal(name, macro.Name);
        Assert.Equal(isQuoted, macro.IsQuoted);
        Assert.Equal(isRequired, macro.IsRequired);
        Assert.Equal(defaultText, macro.DefaultText);
        Assert.Null(macro.MalformedModifierLabel);
    }

    [Fact]
    public void ARequiredLeftInTheNameByARepeatedQuoteIsMalformed()
    {
        var macro = Assert.Single(ScriptMacro.Find("{{env:X|required|quote|quote}}"));

        Assert.Equal("X|required|quote", macro.Name);
        Assert.Equal("X", macro.MalformedModifierLabel);
    }

    [Fact]
    public void ARepeatedRequiredLeavesTheModifierInTheNameAndIsMalformedOnEveryType()
    {
        foreach (var text in new[] { "{{env:A|required|required}}", "{{secret:A|required|required}}", "{{prompt:A|required|required}}" })
        {
            var macro = Assert.Single(ScriptMacro.Find(text));

            Assert.Equal("A|required", macro.Name);
            Assert.Equal("A", macro.MalformedModifierLabel);
            Assert.True(macro.IsRejected);
        }
    }

    [Fact]
    public void ValueRequestsMarkALabelRequiredWhenAnyOfItsMacrosIsRequired()
    {
        var requests = ScriptMacro.ValueRequests(ScriptMacro.Find("{{prompt:A}} {{prompt:A|required}} {{secret:B}} {{prompt:C|required|default=x}}"));

        Assert.Equal(
            new[] { new ScriptValueRequest("A", false, null, true), new ScriptValueRequest("B", true, null, false), new ScriptValueRequest("C", false, "x", true) },
            requests);
    }

    [Fact]
    public void DisplayShowsTheRequiredModifier()
    {
        Assert.Equal("{{secret:S|quote|required}}", Assert.Single(ScriptMacro.Find("{{secret:S|required|quote}}")).Display);
    }
}

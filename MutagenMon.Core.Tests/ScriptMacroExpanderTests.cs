using MutagenMon.Core.Scripts;
using MutagenMon.Core.Status;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ScriptMacroExpanderTests
{
    private static readonly SessionSummaryRow Session = new(
        "Appman", "green", "Watching for changes", 2, @"C:\sources\appman", "robbie:sources/appman",
        new DateTimeOffset(2026, 11, 8, 12, 30, 0, TimeSpan.Zero));

    private static ScriptMacroSources Sources(
        IDictionary<string, string>? environment = null,
        IDictionary<(string Key, string Value), string>? registry = null,
        IDictionary<string, string>? credentials = null,
        IDictionary<string, string>? secrets = null,
        SessionSummaryRow? session = null) => new(
        name_ => environment is not null && environment.TryGetValue(name_, out var value) ? value : null,
        (key_, valueName_) => registry is not null && registry.TryGetValue((key_, valueName_), out var value) ? value : null,
        target_ => credentials is not null && credentials.TryGetValue(target_, out var value) ? value : null,
        label_ => secrets is not null && secrets.TryGetValue(label_, out var value) ? value : null,
        session);

    private static Dictionary<string, string> Env(string name, string value) => new() { [name] = value };

    [Fact]
    public void ATextWithoutMacroIsReturnedUnchanged()
    {
        var expansion = ScriptMacroExpander.Expand("echo ${HOME} $(id)", Sources());

        Assert.True(expansion.IsSuccess);
        Assert.Equal("echo ${HOME} $(id)", expansion.Text);
        Assert.Empty(expansion.SecretsToMask);
    }

    [Fact]
    public void AMacroInACommentIsNeitherExpandedNorReportedAndStaysAsWritten()
    {
        const string text = "#echo \"{{reg:HKCU\\Software\\MyAppTest:Blob}}\"\necho {{env:A}} # {{env:UNKNOWN}} {{nope:x}}";

        var expansion = ScriptMacroExpander.Expand(text, Sources(Env("A", "1")));

        Assert.True(expansion.IsSuccess);
        Assert.Equal("#echo \"{{reg:HKCU\\Software\\MyAppTest:Blob}}\"\necho 1 # {{env:UNKNOWN}} {{nope:x}}", expansion.Text);
        Assert.Equal(["1"], expansion.SecretsToMask);
    }

    [Fact]
    public void InsertsAnEnvironmentValueRawAndVerbatim()
    {
        var expansion = ScriptMacroExpander.Expand("mysql -p'{{env:DB_PWD}}'", Sources(Env("DB_PWD", "a b$c")));

        Assert.True(expansion.IsSuccess);
        Assert.Equal("mysql -p'a b$c'", expansion.Text);
    }

    [Fact]
    public void QuoteModifierQuotesAValueContainingQuotesAndCommandSubstitution()
    {
        var expansion = ScriptMacroExpander.Expand("echo {{env:V|quote}}", Sources(Env("V", "a'b$(id)")));

        Assert.Equal("echo 'a'\\''b$(id)'", expansion.Text);
    }

    [Fact]
    public void ReplacesSeveralAndAdjacentMacrosKeepingTheTextAround()
    {
        var sources = Sources(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, session: Session);

        var expansion = ScriptMacroExpander.Expand("x{{env:A}}{{env:B}}-{{session:name}}y", sources);

        Assert.Equal("x12-Appmany", expansion.Text);
    }

    [Fact]
    public void ReportsAMissingEnvironmentVariableByNameWithoutRunningAnything()
    {
        var expansion = ScriptMacroExpander.Expand("echo {{env:DB_PWD}}", Sources());

        Assert.False(expansion.IsSuccess);
        Assert.Equal("", expansion.Text);
        var error = Assert.Single(expansion.Errors);
        Assert.Contains("{{env:DB_PWD}}", error);
        Assert.Contains("DB_PWD", error);
    }

    [Fact]
    public void AnEmptyEnvironmentValueIsAnError()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:V}}", Sources(Env("V", "")));

        Assert.False(expansion.IsSuccess);
    }

    [Fact]
    public void AnEmptyMacroNameIsAnErrorForEveryType()
    {
        var sources = Sources(Env("", "x"), credentials: Env("", "x"), secrets: Env("", "x"), session: Session);

        var expansion = ScriptMacroExpander.Expand("{{env:}} {{cred:}} {{secret:}} {{session:}}", sources);

        Assert.Equal(4, expansion.Errors.Count);
    }

    [Fact]
    public void ListsEveryUnresolvedMacroNotJustTheFirst()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:A}} {{env:B}} {{foo:C}}", Sources());

        Assert.Equal(3, expansion.Errors.Count);
        Assert.Contains("{{env:A}}", expansion.Errors[0]);
        Assert.Contains("{{env:B}}", expansion.Errors[1]);
        Assert.Contains("{{foo:C}}", expansion.Errors[2]);
    }

    [Fact]
    public void ARepeatedUnresolvedMacroIsReportedOnce()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:A}} {{env:A}} {{secret:T}} {{secret:T|quote}}", Sources());

        Assert.Equal(2, expansion.Errors.Count);
        Assert.Contains("{{env:A}}", expansion.Errors[0]);
        Assert.Contains("{{secret:T}}", expansion.Errors[1]);
    }

    [Fact]
    public void AMacroRepeatedInTheScriptIsReadOnlyOnce()
    {
        var reads = 0;
        var sources = Sources() with
        {
            Credential = target_ =>
            {
                reads++;
                return "pwd-" + target_;
            },
        };

        var expansion = ScriptMacroExpander.Expand("{{cred:T}} {{cred:T}} {{cred:T|quote}}", sources);

        Assert.Equal(1, reads);
        Assert.Equal("pwd-T pwd-T 'pwd-T'", expansion.Text);
    }

    [Fact]
    public void AnUnclosedTokenDoesNotHideTheFollowingMacroFromTheErrorMessage()
    {
        var expansion = ScriptMacroExpander.Expand("echo \"{{env:X\" ; echo {{env:Y}}", Sources());

        var error = Assert.Single(expansion.Errors);
        Assert.Contains("{{env:Y}}", error);
    }

    [Fact]
    public void AnUnknownTypeIsAnError()
    {
        var expansion = ScriptMacroExpander.Expand("{{foo:bar}}", Sources());

        var error = Assert.Single(expansion.Errors);
        Assert.Contains("unknown macro type 'foo'", error);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void RefusesAValueWithALineBreakOrNulRawOrQuoted(string value)
    {
        var sources = Sources(Env("V", value));

        Assert.False(ScriptMacroExpander.Expand("{{env:V}}", sources).IsSuccess);
        Assert.False(ScriptMacroExpander.Expand("{{env:V|quote}}", sources).IsSuccess);
    }

    [Fact]
    public void ErrorsNameTheMacroButNeverCarryTheValue()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:V}}", Sources(Env("V", "top\nsecret")));

        var error = Assert.Single(expansion.Errors);
        Assert.Contains("{{env:V}}", error);
        Assert.DoesNotContain("top", error);
        Assert.DoesNotContain("secret", error);
    }

    [Fact]
    public void SessionMacroReadsThePropertyIgnoringCase()
    {
        var expansion = ScriptMacroExpander.Expand(
            "{{session:name}} {{session:Status}} {{session:alphaurl}} {{session:conflictCount}}", Sources(session: Session));

        Assert.True(expansion.IsSuccess);
        Assert.Equal(@"Appman Watching for changes C:\sources\appman 2", expansion.Text);
    }

    [Fact]
    public void SessionMacroSupportsEveryPublicPropertyOfTheRow()
    {
        var properties = typeof(SessionSummaryRow).GetProperties().Select(property_ => property_.Name);

        foreach (var property in properties)
        {
            var expansion = ScriptMacroExpander.Expand("{{session:" + property + "}}", Sources(session: Session));

            Assert.True(expansion.IsSuccess, property);
        }
    }

    [Fact]
    public void SessionMacroFormatsTheDateAsIso8601AndNoDateAsEmpty()
    {
        var withDate = ScriptMacroExpander.Expand("{{session:LastChangedUtc}}", Sources(session: Session));
        var withoutDate = ScriptMacroExpander.Expand("[{{session:LastChangedUtc}}]", Sources(session: Session with { LastChangedUtc = null }));

        Assert.Equal("2026-11-08T12:30:00.0000000+00:00", withDate.Text);
        Assert.Equal("[]", withoutDate.Text);
    }

    [Fact]
    public void SessionMacroWithoutSelectedSessionIsAnError()
    {
        var expansion = ScriptMacroExpander.Expand("{{session:name}}", Sources());

        var error = Assert.Single(expansion.Errors);
        Assert.Contains("{{session:name}}", error);
        Assert.Contains("no session is selected", error);
    }

    [Fact]
    public void SessionMacroWithAnUnknownPropertyIsAnError()
    {
        var expansion = ScriptMacroExpander.Expand("{{session:nosuchproperty}}", Sources(session: Session));

        var error = Assert.Single(expansion.Errors);
        Assert.Contains("unknown session property 'nosuchproperty'", error);
    }

    [Fact]
    public void SessionValuesAreNotMasked()
    {
        var expansion = ScriptMacroExpander.Expand("{{session:name}}", Sources(session: Session));

        Assert.Empty(expansion.SecretsToMask);
    }

    [Fact]
    public void AnEmptySessionValueIsAllowed()
    {
        var expansion = ScriptMacroExpander.Expand("[{{session:ConflictDisplay}}]", Sources(session: Session with { ConflictCount = 0 }));

        Assert.True(expansion.IsSuccess);
        Assert.Equal("[]", expansion.Text);
    }

    [Fact]
    public void RegistryMacroSplitsTheKeyAndTheValueNameAtTheLastColon()
    {
        var registry = new Dictionary<(string Key, string Value), string> { [(@"HKCU\Software\My:App", "DbPassword")] = "s3cret" };

        var expansion = ScriptMacroExpander.Expand(@"{{reg:HKCU\Software\My:App:DbPassword}}", Sources(registry: registry));

        Assert.True(expansion.IsSuccess);
        Assert.Equal("s3cret", expansion.Text);
    }

    [Theory]
    [InlineData(@"{{reg:HKEY_CURRENT_USER\Software\MyApp:V}}", @"HKCU\Software\MyApp")]
    [InlineData(@"{{reg:hkcu\Software\MyApp:V}}", @"HKCU\Software\MyApp")]
    [InlineData(@"{{reg:HKLM\Software\MyApp:V}}", @"HKLM\Software\MyApp")]
    [InlineData(@"{{reg:HKEY_LOCAL_MACHINE\Software\MyApp:V}}", @"HKLM\Software\MyApp")]
    public void RegistryMacroPassesTheShortHiveNameToTheReader(string text, string expectedKey)
    {
        var registry = new Dictionary<(string Key, string Value), string> { [(expectedKey, "V")] = "found" };

        var expansion = ScriptMacroExpander.Expand(text, Sources(registry: registry));

        Assert.True(expansion.IsSuccess);
        Assert.Equal("found", expansion.Text);
    }

    [Theory]
    [InlineData(@"{{reg:HKCR\Software:V}}")]
    [InlineData(@"{{reg:HKEY_USERS\Software:V}}")]
    [InlineData(@"{{reg:Software\MyApp:V}}")]
    public void RegistryMacroWithAnUnknownHiveIsAnError(string text)
    {
        var expansion = ScriptMacroExpander.Expand(text, Sources());

        var error = Assert.Single(expansion.Errors);
        Assert.Contains("unknown registry hive", error);
    }

    [Theory]
    [InlineData("{{reg:HKCU}}")]
    [InlineData(@"{{reg:HKCU\Software}}")]
    [InlineData(@"{{reg::V}}")]
    [InlineData(@"{{reg:HKCU\Software:}}")]
    [InlineData(@"{{reg:HKCU:V}}")]
    [InlineData(@"{{reg:HKCU\:V}}")]
    public void RegistryMacroWithAnEmptyKeyOrValueNameIsAnError(string text)
    {
        var expansion = ScriptMacroExpander.Expand(text, Sources());

        Assert.False(expansion.IsSuccess);
    }

    [Fact]
    public void RegistryMacroWithAMissingValueIsAnErrorNamingTheMacro()
    {
        var expansion = ScriptMacroExpander.Expand(@"{{reg:HKCU\Software\MyApp:Missing}}", Sources());

        var error = Assert.Single(expansion.Errors);
        Assert.Contains(@"{{reg:HKCU\Software\MyApp:Missing}}", error);
    }

    [Fact]
    public void CredentialMacroKeepsATargetWithColonsSlashesAndBackslashesWhole()
    {
        var credentials = Env(@"Domain\app:prod/db", "pwd");

        var expansion = ScriptMacroExpander.Expand(@"{{cred:Domain\app:prod/db}}", Sources(credentials: credentials));

        Assert.Equal("pwd", expansion.Text);
    }

    [Fact]
    public void CredentialMacroWithAMissingTargetOrEmptyPasswordIsAnError()
    {
        Assert.False(ScriptMacroExpander.Expand("{{cred:Missing}}", Sources()).IsSuccess);
        Assert.False(ScriptMacroExpander.Expand("{{cred:T}}", Sources(credentials: Env("T", ""))).IsSuccess);
    }

    [Fact]
    public void SecretMacroUsesTheSameValueForTheSameLabel()
    {
        var expansion = ScriptMacroExpander.Expand(
            "{{secret:Token}} {{secret:Token|quote}}", Sources(secrets: Env("Token", "a b")));

        Assert.Equal("a b 'a b'", expansion.Text);
        Assert.Equal(new[] { "a b" }, expansion.SecretsToMask);
    }

    [Fact]
    public void AQuotedSecretWithASingleQuoteIsAlsoMaskedInItsQuotedFormBecauseSetXTracesIt()
    {
        var expansion = ScriptMacroExpander.Expand("{{secret:S|quote}}", Sources(secrets: Env("S", "a'b")));

        Assert.Equal("'a'\\''b'", expansion.Text);
        Assert.Equal(new[] { "a'b", "a'\\''b" }, expansion.SecretsToMask);
        Assert.Equal("+ echo '***'", new ScriptOutputMasker(expansion.SecretsToMask).Mask("+ echo 'a'\\''b'"));
    }

    [Fact]
    public void ASecretWithASingleQuoteIsMaskedInAWordThatBashTracesAsAWhole()
    {
        // bash -x traces: + mysql '-ppa'\''ss'
        var raw = ScriptMacroExpander.Expand("mysql -p'{{env:E}}'", Sources(Env("E", "pa'ss")));
        var quoted = ScriptMacroExpander.Expand("mysql -p{{env:E|quote}}", Sources(Env("E", "pa'ss")));

        Assert.Equal("+ mysql '-p***'", new ScriptOutputMasker(raw.SecretsToMask).Mask("+ mysql '-ppa'\\''ss'"));
        Assert.Equal("+ mysql '-p***'", new ScriptOutputMasker(quoted.SecretsToMask).Mask("+ mysql '-ppa'\\''ss'"));
    }

    [Fact]
    public void ARawSecretWithASingleQuoteIsAlsoMaskedInItsQuotedForm()
    {
        var expansion = ScriptMacroExpander.Expand("P=\"{{env:E}}\"", Sources(Env("E", "pa'ss")));

        Assert.Equal("P=\"pa'ss\"", expansion.Text);
        Assert.Equal(new[] { "pa'ss", "pa'\\''ss" }, expansion.SecretsToMask);
        Assert.Equal("+ echo '***'", new ScriptOutputMasker(expansion.SecretsToMask).Mask("+ echo 'pa'\\''ss'"));
    }

    [Fact]
    public void ASecretWithSpacesButNoSingleQuoteIsMaskedThroughItsRawForm()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:E}}", Sources(Env("E", "a b")));

        Assert.Equal("+ echo '***'", new ScriptOutputMasker(expansion.SecretsToMask).Mask("+ echo 'a b'"));
    }

    [Fact]
    public void AMistypedTypeWithADigitIsReportedAsUnknownNotIgnored()
    {
        var expansion = ScriptMacroExpander.Expand("echo {{env2:X}}", Sources(Env("X", "v")));

        var error = Assert.Single(expansion.Errors);
        Assert.Contains("unknown macro type 'env2'", error);
    }

    [Fact]
    public void AnEmptyNameIsReportedAsSuchNotAsAMissingValue()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:}} {{cred:}} {{secret:}} {{session:}}", Sources(session: Session));

        Assert.Equal(4, expansion.Errors.Count);
        Assert.All(expansion.Errors, error_ => Assert.Contains("the name is empty", error_));
    }

    [Fact]
    public void ARefusedValueUsedRawAndQuotedIsReportedOnce()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:V}} {{env:V|quote}}", Sources(Env("V", "a\nb")));

        Assert.Single(expansion.Errors);
    }

    [Fact]
    public void SecretMacroWithoutAValueOrWithAnEmptyValueIsAnError()
    {
        Assert.False(ScriptMacroExpander.Expand("{{secret:Token}}", Sources()).IsSuccess);
        Assert.False(ScriptMacroExpander.Expand("{{secret:Token}}", Sources(secrets: Env("Token", ""))).IsSuccess);
    }

    [Fact]
    public void EnvRegCredAndSecretValuesAreCollectedForMaskingEachOnce()
    {
        var sources = Sources(
            Env("E", "env-value"),
            new Dictionary<(string Key, string Value), string> { [(@"HKCU\K", "V")] = "reg-value" },
            Env("C", "cred-value"),
            Env("S", "secret-value"),
            Session);

        var expansion = ScriptMacroExpander.Expand(
            @"{{env:E}} {{env:E}} {{reg:HKCU\K:V}} {{cred:C}} {{secret:S}} {{session:name}}", sources);

        Assert.True(expansion.IsSuccess);
        Assert.Equal(new[] { "env-value", "reg-value", "cred-value", "secret-value" }, expansion.SecretsToMask);
    }

    [Fact]
    public void AFailedExpansionCollectsNoSecretToMask()
    {
        var expansion = ScriptMacroExpander.Expand("{{env:E}} {{env:Missing}}", Sources(Env("E", "value")));

        Assert.False(expansion.IsSuccess);
        Assert.Empty(expansion.SecretsToMask);
    }

    [Fact]
    public void ToStringNeverPrintsTheExpandedTextOrTheSecrets()
    {
        var expansion = new ScriptMacroExpansion("echo hunter2", [], ["hunter2"]);

        var text = expansion.ToString();

        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("IsSuccess = True", text);
    }
}

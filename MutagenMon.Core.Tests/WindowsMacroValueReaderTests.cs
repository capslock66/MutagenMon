using System.Runtime.Versioning;
using Microsoft.Win32;
using MutagenMon.Core.Scripts;
using Xunit;

namespace MutagenMon.Core.Tests;

// The attribute only satisfies the platform analyzer (CA1416: RegistryValueKind is annotated Windows-only);
// the pure formatting and expansion tests run on every platform, and the tests that need Windows skip themselves elsewhere.
[SupportedOSPlatform("windows")]
public class WindowsMacroValueReaderTests
{
    private static string Upper(string text) => text.ToUpperInvariant();

    /// <summary>A fake store of environment variables: plain text, or text with references to expand.</summary>
    private static Func<string, WindowsMacroValueReader.RawEnvironmentValue?> Store(params (string Name, string Text, bool IsExpandable)[] variables)
    {
        var byName = variables.ToDictionary(variable_ => variable_.Name, StringComparer.OrdinalIgnoreCase);
        return name_ => byName.TryGetValue(name_, out var variable)
            ? new WindowsMacroValueReader.RawEnvironmentValue(variable.Text, variable.IsExpandable)
            : null;
    }

    [Fact]
    public void RegSzIsReturnedVerbatimAndNotExpanded()
    {
        var text = WindowsMacroValueReader.FormatRegistryValue("a %X% b", RegistryValueKind.String, Upper);

        Assert.Equal("a %X% b", text);
    }

    [Fact]
    public void RegExpandSzIsExpandedWithTheGivenFunction()
    {
        var text = WindowsMacroValueReader.FormatRegistryValue("a %x% b", RegistryValueKind.ExpandString, Upper);

        Assert.Equal("A %X% B", text);
    }

    [Theory]
    [InlineData(3306, "3306")]
    [InlineData(0, "0")]
    [InlineData(-1, "4294967295")]
    public void RegDwordIsReturnedAsUnsignedDecimalText(int stored, string expected)
    {
        Assert.Equal(expected, WindowsMacroValueReader.FormatRegistryValue(stored, RegistryValueKind.DWord, Upper));
    }

    [Theory]
    [InlineData(5000000000L, "5000000000")]
    [InlineData(-1L, "18446744073709551615")]
    public void RegQwordIsReturnedAsUnsignedDecimalText(long stored, string expected)
    {
        Assert.Equal(expected, WindowsMacroValueReader.FormatRegistryValue(stored, RegistryValueKind.QWord, Upper));
    }

    [Fact]
    public void UnsupportedKindsAndMissingValuesGiveNull()
    {
        Assert.Null(WindowsMacroValueReader.FormatRegistryValue(new byte[] { 1, 2 }, RegistryValueKind.Binary, Upper));
        Assert.Null(WindowsMacroValueReader.FormatRegistryValue(new[] { "a", "b" }, RegistryValueKind.MultiString, Upper));
        Assert.Null(WindowsMacroValueReader.FormatRegistryValue(null, RegistryValueKind.String, Upper));
        Assert.Null(WindowsMacroValueReader.FormatRegistryValue(5, RegistryValueKind.String, Upper));
    }

    [Fact]
    public void ExpandReferencesReplacesKnownReferencesAndLeavesUnknownOnesAsWritten()
    {
        var values = new Dictionary<string, string> { ["HOME"] = @"C:\Users\x", ["SUFFIX"] = "dev" };

        var text = WindowsMacroValueReader.ExpandReferences(
            @"%HOME%\bin-%SUFFIX%-%UNKNOWN%", name_ => values.GetValueOrDefault(name_));

        Assert.Equal(@"C:\Users\x\bin-dev-%UNKNOWN%", text);
    }

    [Theory]
    [InlineData("100% sure")]
    [InlineData("%% done")]
    [InlineData("100% sure %% done")]
    [InlineData("%")]
    public void ExpandReferencesLeavesALoneOrEmptyPercentSignAsWritten(string text)
    {
        // No variable is known: "% sure %" is read as a reference to a variable named " sure " and stays as written.
        Assert.Equal(text, WindowsMacroValueReader.ExpandReferences(text, _ => null));
    }

    [Fact]
    public void ExpandReferencesExpandsAReferenceThatFollowsALonePercentSign()
    {
        var values = new Dictionary<string, string> { ["HOME"] = @"C:\Users\x" };

        var text = WindowsMacroValueReader.ExpandReferences("50% of %HOME%", name_ => values.GetValueOrDefault(name_));

        Assert.Equal(@"50% of C:\Users\x", text);
    }

    [Fact]
    public void ExpandReferencesNeverLooksUpANameContainingALineBreak()
    {
        // The lookup would answer for a name with a line break: it must not even be asked.
        var text = WindowsMacroValueReader.ExpandReferences(
            "%A\nB% and %C\rD%", name_ => name_.Contains('\n') || name_.Contains('\r') ? "X" : null);

        Assert.Equal("%A\nB% and %C\rD%", text);
    }

    [Fact]
    public void DecodeCredentialBlobReadsUtf16Text()
    {
        Assert.Equal("s3cret", WindowsMacroValueReader.DecodeCredentialBlob(System.Text.Encoding.Unicode.GetBytes("s3cret")));
        Assert.Equal("pässwörd-密码", WindowsMacroValueReader.DecodeCredentialBlob(System.Text.Encoding.Unicode.GetBytes("pässwörd-密码")));
    }

    [Fact]
    public void DecodeCredentialBlobDropsTrailingNullCharacters()
    {
        Assert.Equal("pwd", WindowsMacroValueReader.DecodeCredentialBlob(System.Text.Encoding.Unicode.GetBytes("pwd\0")));
        Assert.Equal("pwd", WindowsMacroValueReader.DecodeCredentialBlob(System.Text.Encoding.Unicode.GetBytes("pwd\0\0")));
    }

    [Fact]
    public void DecodeCredentialBlobRejectsInvalidUtf16InsteadOfReplacingCharacters()
    {
        // 0xD800 is a lone high surrogate: decoding it leniently would give U+FFFD, i.e. a wrong password without any error.
        Assert.Null(WindowsMacroValueReader.DecodeCredentialBlob(new byte[] { 0x00, 0xD8 }));
        Assert.Null(WindowsMacroValueReader.DecodeCredentialBlob(new byte[] { 0x61, 0x00, 0x00, 0xD8 }));
    }

    [Fact]
    public void ExpandReferencesExpandsAdjacentReferences()
    {
        var values = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };

        Assert.Equal("12", WindowsMacroValueReader.ExpandReferences("%A%%B%", name_ => values.GetValueOrDefault(name_)));
    }

    [Fact]
    public void ExpandReferencesReusesTheClosingPercentOfAnUnknownReferenceAsTheNextOpeningOne()
    {
        // The rule of the managed Environment.ExpandEnvironmentVariables; to be compared with Windows (findings.md).
        var values = new Dictionary<string, string> { ["HOME"] = "H" };

        Assert.Equal("%UNKNOWNH", WindowsMacroValueReader.ExpandReferences("%UNKNOWN%HOME%", name_ => values.GetValueOrDefault(name_)));
    }

    [Fact]
    public void DecodeCredentialBlobRejectsEmptyOddSizedAndNullOnlyBlobs()
    {
        Assert.Null(WindowsMacroValueReader.DecodeCredentialBlob(ReadOnlySpan<byte>.Empty));
        Assert.Null(WindowsMacroValueReader.DecodeCredentialBlob(new byte[] { 0x41 }));
        // UTF-8 text of odd length: not UTF-16, unreadable.
        Assert.Null(WindowsMacroValueReader.DecodeCredentialBlob(System.Text.Encoding.UTF8.GetBytes("abc")));
        Assert.Null(WindowsMacroValueReader.DecodeCredentialBlob(new byte[] { 0, 0, 0, 0 }));
    }

    [Fact]
    public void ResolveReturnsAPlainValueAsIsAndNullForAnUnknownVariable()
    {
        var store = Store(("A", "plain %B% text", false));

        Assert.Equal("plain %B% text", WindowsMacroValueReader.ResolveEnvironmentVariable("a", store));
        Assert.Null(WindowsMacroValueReader.ResolveEnvironmentVariable("MISSING", store));
    }

    [Fact]
    public void ResolveExpandsReferencesThroughSeveralVariables()
    {
        var store = Store(("TOOLS", @"%ROOT%\tools", true), ("ROOT", @"C:\apps", true), ("PATH_EXTRA", "%TOOLS%;%MISSING%", true));

        Assert.Equal(@"C:\apps\tools;%MISSING%", WindowsMacroValueReader.ResolveEnvironmentVariable("PATH_EXTRA", store));
    }

    [Fact]
    public void ResolveDoesNotExpandAgainAValueThatIsNotExpandable()
    {
        var store = Store(("A", "%B%", true), ("B", "literal %C%", false), ("C", "never", true));

        Assert.Equal("literal %C%", WindowsMacroValueReader.ResolveEnvironmentVariable("A", store));
    }

    [Fact]
    public void ResolveLeavesASelfReferenceAsWrittenInsteadOfRepeatingTheTail()
    {
        // The real-world case: a user Path defined as %Path%;C:\tools.
        var store = Store(("Path", @"%Path%;C:\tools", true));

        Assert.Equal(@"%Path%;C:\tools", WindowsMacroValueReader.ResolveEnvironmentVariable("Path", store));
    }

    [Fact]
    public void ResolveLeavesAReferenceCycleAsWritten()
    {
        var store = Store(("A", "%B%", true), ("B", "%A%", true));

        Assert.Equal("%A%", WindowsMacroValueReader.ResolveEnvironmentVariable("A", store));
    }

    [Fact]
    public void ResolveStopsAfterFiveLevelsOfReferences()
    {
        var store = Store(
            ("V0", "%V1%", true), ("V1", "%V2%", true), ("V2", "%V3%", true), ("V3", "%V4%", true),
            ("V4", "%V5%", true), ("V5", "%V6%", true), ("V6", "end", true));

        Assert.Equal("%V6%", WindowsMacroValueReader.ResolveEnvironmentVariable("V0", store));
        Assert.Equal("end", WindowsMacroValueReader.ResolveEnvironmentVariable("V6", store));
    }

    [Fact]
    public void EnvironmentVariableFallsBackToTheProcessEnvironment()
    {
        var name = "MUTAGENMON_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "from-process");
        try
        {
            Assert.Equal("from-process", WindowsMacroValueReader.ReadEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AProcessEnvironmentValueIsNotExpandedAgain()
    {
        var name = "MUTAGENMON_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "100%HOME% literal");
        try
        {
            Assert.Equal("100%HOME% literal", WindowsMacroValueReader.ReadEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AnUnknownEnvironmentVariableIsNull()
    {
        Assert.Null(WindowsMacroValueReader.ReadEnvironmentVariable("MUTAGENMON_NO_SUCH_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void RegistryAndCredentialAreNullOffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The registry and the Credential Manager exist on Windows.");

        Assert.Null(WindowsMacroValueReader.ReadRegistryValue(@"HKCU\Software", "Anything"));
        Assert.Null(WindowsMacroValueReader.ReadCredentialPassword("Anything"));
    }

    [Fact]
    public void ReadsRealRegistryValuesOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry test.");

        var subKey = @"Software\MutagenMonTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(subKey))
            {
                key.SetValue("Text", "s3cret", RegistryValueKind.String);
                key.SetValue("Expand", "%MUTAGENMON_TEST_REGISTRY%-x", RegistryValueKind.ExpandString);
                key.SetValue("Port", 3306, RegistryValueKind.DWord);
                key.SetValue("Big", 5000000000L, RegistryValueKind.QWord);
                key.SetValue("Blob", new byte[] { 1, 2 }, RegistryValueKind.Binary);
            }

            Environment.SetEnvironmentVariable("MUTAGENMON_TEST_REGISTRY", "expanded");
            var fullKey = @"HKCU\" + subKey;
            Assert.Equal("s3cret", WindowsMacroValueReader.ReadRegistryValue(fullKey, "Text"));
            Assert.Equal("expanded-x", WindowsMacroValueReader.ReadRegistryValue(fullKey, "Expand"));
            Assert.Equal("3306", WindowsMacroValueReader.ReadRegistryValue(fullKey, "Port"));
            Assert.Equal("5000000000", WindowsMacroValueReader.ReadRegistryValue(fullKey, "Big"));
            Assert.Null(WindowsMacroValueReader.ReadRegistryValue(fullKey, "Blob"));
            Assert.Null(WindowsMacroValueReader.ReadRegistryValue(fullKey, "Missing"));
            Assert.Null(WindowsMacroValueReader.ReadRegistryValue(@"HKCU\Software\MutagenMonTests\no-such-key", "Text"));
            Assert.Null(WindowsMacroValueReader.ReadRegistryValue(@"HKCR\Software", "Text"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUTAGENMON_TEST_REGISTRY", null);
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            DeleteEmptyTestParentKey();
        }
    }

    [Fact]
    public void ReadsAnEnvironmentVariableFromTheUserRegistryOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry test.");

        var suffix = Guid.NewGuid().ToString("N");
        var expanding = "MUTAGENMON_TEST_ENV_" + suffix;
        var unsupported = "MUTAGENMON_TEST_BIN_" + suffix;
        var referenced = "MUTAGENMON_TEST_REF_" + suffix;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)!)
            {
                key.SetValue(expanding, "%" + referenced + "%-x", RegistryValueKind.ExpandString);
                key.SetValue(unsupported, new byte[] { 1, 2 }, RegistryValueKind.Binary);
            }

            Environment.SetEnvironmentVariable(referenced, "from-process");
            Environment.SetEnvironmentVariable(unsupported, "process-value");

            // A REG_EXPAND_SZ user value is expanded, its reference being resolved through the process environment.
            Assert.Equal("from-process-x", WindowsMacroValueReader.ReadEnvironmentVariable(expanding));
            // A user value of an unsupported type is ignored as if absent: the process value answers.
            Assert.Equal("process-value", WindowsMacroValueReader.ReadEnvironmentVariable(unsupported));
        }
        finally
        {
            Environment.SetEnvironmentVariable(referenced, null);
            Environment.SetEnvironmentVariable(unsupported, null);
            using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            key?.DeleteValue(expanding, throwOnMissingValue: false);
            key?.DeleteValue(unsupported, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void ReadsAMachineRegistryValueOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry test.");

        // CurrentBuild is a REG_SZ present on every Windows installation.
        var build = WindowsMacroValueReader.ReadRegistryValue(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild");

        Assert.False(string.IsNullOrEmpty(build));
    }

    [Fact]
    public void AMissingCredentialIsNullOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Credential Manager test.");

        Assert.Null(WindowsMacroValueReader.ReadCredentialPassword("MutagenMonTests/no-such-credential-" + Guid.NewGuid().ToString("N")));
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteEmptyTestParentKey()
    {
        const string parentPath = @"Software\MutagenMonTests";
        bool isEmpty;
        using (var parent = Registry.CurrentUser.OpenSubKey(parentPath))
            isEmpty = parent is { SubKeyCount: 0, ValueCount: 0 };

        if (isEmpty)
            Registry.CurrentUser.DeleteSubKey(parentPath, throwOnMissingSubKey: false);
    }
}

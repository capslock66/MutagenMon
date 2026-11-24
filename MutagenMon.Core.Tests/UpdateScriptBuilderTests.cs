using MutagenMon.Core.Updates;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-66.7: the script that replaces the files once the process has exited.</summary>
public class UpdateScriptBuilderTests
{
    private const string Deploy = @"\\prod-build\DATA\install\Mutagen-mon\";
    private const string Install = @"c:\tools\Mutagen-Mon";
    private const string Exe = @"c:\tools\Mutagen-Mon\MutagenMon.exe";

    [Fact]
    public void WaitsForTheProcessCopiesTheFilesThenRestarts()
    {
        var script = UpdateScriptBuilder.Build(4242, Deploy, Install, Exe, @"c:\tmp\update.log");

        Assert.Contains("PID eq 4242", script);
        Assert.Contains(@"robocopy ""\\prod-build\DATA\install\Mutagen-mon"" ""c:\tools\Mutagen-Mon"" *.*", script);
        Assert.Contains(@"""\\prod-build\DATA\install\Mutagen-mon\Assets"" ""c:\tools\Mutagen-Mon\Assets"" /E", script);
        Assert.True(script.IndexOf("robocopy", StringComparison.Ordinal) < script.IndexOf("start \"\"", StringComparison.Ordinal));
        Assert.Contains(@"start """" ""c:\tools\Mutagen-Mon\MutagenMon.exe""", script);
    }

    [Fact]
    public void NeverTouchesTheConfigAndMutagenFolders()
    {
        var script = UpdateScriptBuilder.Build(1, Deploy, Install, Exe, @"c:\tmp\update.log");

        // No /E on the top-level copy: only the Assets subfolder is copied recursively.
        Assert.Equal(1, script.Split("/E").Length - 1);
        Assert.DoesNotContain(@"\config", script);
        Assert.DoesNotContain(@"\mutagen""", script);
    }

    [Fact]
    public void UsesWindowsLineEndings()
    {
        var script = UpdateScriptBuilder.Build(1, Deploy, Install, Exe, @"c:\tmp\update.log");

        Assert.DoesNotContain("\n", script.Replace("\r\n", ""));
    }

    [Fact]
    public void DoublesPercentSignsAndRejectsQuotes()
    {
        var script = UpdateScriptBuilder.Build(1, @"c:\a%b", Install, Exe, @"c:\tmp\update.log");

        Assert.Contains(@"""c:\a%%b""", script);
        Assert.Throws<ArgumentException>(() => UpdateScriptBuilder.Build(1, "c:\\a\"b", Install, Exe, @"c:\tmp\update.log"));
    }

    [Fact]
    public void KeepsAccentedPathsIntactAndGuardsAnIncompleteSource()
    {
        var script = UpdateScriptBuilder.Build(1, Deploy, Install, Exe, @"c:\tmp\update.log");

        Assert.Contains("chcp 65001", script);
        Assert.True(script.IndexOf("chcp 65001", StringComparison.Ordinal) < script.IndexOf("MM_LOG", StringComparison.Ordinal));
        // The checks come before the first copy.
        var firstRobocopy = script.IndexOf("robocopy", StringComparison.Ordinal);
        Assert.True(script.IndexOf(@"if not exist ""\\prod-build\DATA\install\Mutagen-mon\MutagenMon.exe""", StringComparison.Ordinal) is > 0 and var exeGuard && exeGuard < firstRobocopy);
        Assert.True(script.IndexOf(@"if not exist ""\\prod-build\DATA\install\Mutagen-mon\Assets""", StringComparison.Ordinal) is > 0 and var assetsGuard && assetsGuard < firstRobocopy);
    }

    [Fact]
    public void ATimeoutDoesNotRestartMutagenMon()
    {
        var script = UpdateScriptBuilder.Build(1, Deploy, Install, Exe, @"c:\tmp\update.log");

        var timeoutBranch = script[script.IndexOf(":timeout", StringComparison.Ordinal)..script.IndexOf(":copy", StringComparison.Ordinal)];
        Assert.Contains("goto end", timeoutBranch);
        Assert.DoesNotContain("start \"\"", timeoutBranch);
        Assert.True(script.IndexOf("start \"\"", StringComparison.Ordinal) < script.IndexOf(":end", StringComparison.Ordinal));
        Assert.True(script.IndexOf(":end", StringComparison.Ordinal) < script.IndexOf("del \"%~f0\"", StringComparison.Ordinal));
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Scripts;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>Runs <see cref="ScriptRunner"/> against a fake `ssh` — a bash
/// script that records its arguments and stdin next to itself, then behaves
/// as the test dictates. The fake needs a POSIX shell, so those tests are
/// skipped on Windows.</summary>
public class ScriptRunnerTests
{
    private sealed record FakeSsh(string Directory, string Path)
    {
        public string[] Arguments => File.ReadAllLines(System.IO.Path.Combine(Directory, "args.txt"));

        public string Stdin => File.ReadAllText(System.IO.Path.Combine(Directory, "stdin.txt"));
    }

    private static FakeSsh CreateFakeSsh(string behavior)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "The fake ssh is a bash script.");

        var directory = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake-ssh");
        File.WriteAllText(path,
            "#!/bin/bash\n" +
            "dir=\"$(dirname \"$0\")\"\n" +
            "printf '%s\\n' \"$@\" > \"$dir/args.txt\"\n" +
            "cat > \"$dir/stdin.txt\"\n" +
            behavior + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FakeSsh(directory, path);
    }

    private static ScriptRunner NewRunner(string sshPath, int timeoutSeconds = 30) =>
        new(Options.Create(new MutagenMonOptions { SshPath = sshPath, ScriptTimeoutSeconds = timeoutSeconds }),
            NullLogger<ScriptRunner>.Instance);

    private static ScriptDefinition NewScript(params string[] extraDirectives)
    {
        var lines = new List<string> { "[test]", "# @host: robbie" };
        lines.AddRange(extraDirectives);
        lines.Add("echo hi");
        return ScriptFileParser.ParseLines(lines).Single();
    }

    [Fact]
    public void BuildScriptTextPrependsSetEWhenStoppingOnError()
    {
        Assert.Equal("set -e\n# @host: robbie\necho hi\n", ScriptRunner.BuildScriptText(NewScript()));
    }

    [Fact]
    public void BuildScriptTextHasNoSetEWhenNotStoppingOnError()
    {
        Assert.Equal("# @host: robbie\n# @stop-on-error: false\necho hi\n", ScriptRunner.BuildScriptText(NewScript("# @stop-on-error: false")));
    }

    [Fact]
    public async Task RunsSshNonInteractivelyWithBashReadingTheScriptFromStdin()
    {
        var ssh = CreateFakeSsh("exit 0");

        await NewRunner(ssh.Path).RunAsync(NewScript(), _ => { }, CancellationToken.None);

        Assert.Equal(new[] { "-o", "BatchMode=yes", "-T", "--", "robbie", "bash", "-s" }, ssh.Arguments);
    }

    [Fact]
    public async Task SendsTheWholeScriptWithSetEOnStdin()
    {
        var ssh = CreateFakeSsh("exit 0");

        await NewRunner(ssh.Path).RunAsync(NewScript(), _ => { }, CancellationToken.None);

        Assert.Equal("set -e\n# @host: robbie\necho hi\n", ssh.Stdin);
    }

    [Fact]
    public async Task OmitsSetEWhenStopOnErrorIsFalse()
    {
        var ssh = CreateFakeSsh("exit 0");

        await NewRunner(ssh.Path).RunAsync(NewScript("# @stop-on-error: false"), _ => { }, CancellationToken.None);

        Assert.DoesNotContain("set -e", ssh.Stdin);
    }

    [Fact]
    public async Task ReportsACompletedRunWithItsExitCode()
    {
        var ssh = CreateFakeSsh("exit 3");

        var result = await NewRunner(ssh.Path).RunAsync(NewScript(), _ => { }, CancellationToken.None);

        Assert.Equal(ScriptRunOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task ReportsExitCodeZeroOnSuccess()
    {
        var ssh = CreateFakeSsh("exit 0");

        var result = await NewRunner(ssh.Path).RunAsync(NewScript(), _ => { }, CancellationToken.None);

        Assert.Equal(ScriptRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task StreamsStdoutAndStderrLinesDistinguishingErrors()
    {
        var ssh = CreateFakeSsh("echo out one\necho err one >&2\necho out two\nexit 0");
        var output = new ConcurrentQueue<ScriptOutputLine>();

        await NewRunner(ssh.Path).RunAsync(NewScript(), output.Enqueue, CancellationToken.None);

        var lines = output.ToArray();
        Assert.Contains(new ScriptOutputLine("out one", false), lines);
        Assert.Contains(new ScriptOutputLine("out two", false), lines);
        Assert.Contains(new ScriptOutputLine("err one", true), lines);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task KillsTheRunWhenTheTimeoutElapses()
    {
        var ssh = CreateFakeSsh("sleep 30");

        var result = await NewRunner(ssh.Path, timeoutSeconds: 1).RunAsync(NewScript(), _ => { }, CancellationToken.None);

        Assert.Equal(ScriptRunOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task KillsTheRunWhenCancelled()
    {
        var ssh = CreateFakeSsh("sleep 30");
        using var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMilliseconds(500));

        var result = await NewRunner(ssh.Path).RunAsync(NewScript(), _ => { }, source.Token);

        Assert.Equal(ScriptRunOutcome.Cancelled, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ReportsFailureToStartWhenSshDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"), "no-such-ssh");

        var result = await NewRunner(missing).RunAsync(NewScript(), _ => { }, CancellationToken.None);

        Assert.Equal(ScriptRunOutcome.FailedToStart, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RefusesAnInvalidScript()
    {
        var invalid = ScriptFileParser.ParseLines(new[] { "[no-host]", "echo hi" }).Single();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewRunner("unused").RunAsync(invalid, _ => { }, CancellationToken.None));
    }
}

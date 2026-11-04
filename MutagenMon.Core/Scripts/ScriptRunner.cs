using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;

namespace MutagenMon.Core.Scripts;

/// <summary>
/// Runs one script on a remote server (FR-50, FR-51): a single
/// `ssh &lt;host&gt; bash -s` process with the whole script written to its
/// standard input, so bash executes it sequentially in one shell. The
/// system ssh binary (<see cref="MutagenMonOptions.SshPath"/>) resolves the
/// host alias itself from `~/.ssh/config`, like the other ssh calls of this
/// app. Shares no state with polling or the tray icon (NFR-1).
/// </summary>
public class ScriptRunner
{
    private static readonly TimeSpan PostKillDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly string _sshPath;
    private readonly TimeSpan _timeout;
    private readonly ILogger<ScriptRunner> _logger;

    public ScriptRunner(IOptions<MutagenMonOptions> options, ILogger<ScriptRunner> logger)
    {
        _sshPath = options.Value.SshPath;
        _timeout = TimeSpan.FromSeconds(options.Value.ScriptTimeoutSeconds);
        _logger = logger;
    }

    /// <summary>Runs <paramref name="script"/> and returns once ssh has
    /// exited, been cancelled via <paramref name="cancellationToken"/>, or
    /// hit the configured timeout. <paramref name="onOutput"/> is invoked on
    /// background threads (FR-52.2) and must not throw. Script output is
    /// never logged (FR-54.2).</summary>
    public async Task<ScriptRunResult> RunAsync(
        ScriptDefinition script, Action<ScriptOutputLine> onOutput, CancellationToken cancellationToken)
    {
        if (!script.IsValid)
            throw new InvalidOperationException($"Script '{script.Name}' cannot run: {script.ValidationError}");

        var host = script.Host!;
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Script '{Script}' starting on {Host}", script.Name, host);

        using var process = new Process { StartInfo = BuildStartInfo(host) };
        process.OutputDataReceived += (_, e) => Publish(e.Data, isError: false, onOutput);
        process.ErrorDataReceived += (_, e) => Publish(e.Data, isError: true, onOutput);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Script '{Script}' on {Host}: failed to start '{SshPath}'", script.Name, host, _sshPath);
            return new ScriptRunResult(ScriptRunOutcome.FailedToStart, null, stopwatch.Elapsed, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout > TimeSpan.Zero)
            runSource.CancelAfter(_timeout);

        // ConfigureAwait(false) throughout: after a cancellation the continuation kills the ssh process tree and waits for it,
        // which can take seconds — it must not be posted back to a caller's UI thread (the output callback is thread-agnostic).
        ScriptRunResult result;
        try
        {
            await WriteScriptAsync(process, BuildScriptText(script), runSource.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(runSource.Token).ConfigureAwait(false);
            result = new ScriptRunResult(ScriptRunOutcome.Completed, process.ExitCode, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            await KillAndDrainAsync(process).ConfigureAwait(false);
            var outcome = cancellationToken.IsCancellationRequested ? ScriptRunOutcome.Cancelled : ScriptRunOutcome.TimedOut;
            result = new ScriptRunResult(outcome, null, stopwatch.Elapsed);
        }

        LogOutcome(script.Name, host, result);
        return result;
    }

    /// <summary>The text sent to bash: the body, with `set -e` prepended
    /// when the script stops on error (FR-51.2).</summary>
    internal static string BuildScriptText(ScriptDefinition script) =>
        (script.StopOnError ? "set -e\n" : "") + script.Body + "\n";

    private ProcessStartInfo BuildStartInfo(string host)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _sshPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // bash must receive UTF-8 without a BOM, whatever the local code page.
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // FR-50.2 / FR-50.4: argument list (no shell), non-interactive, no TTY.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-T");
        // "--" so a host starting with '-' can never be read as an ssh option.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-s");
        return psi;
    }

    private static async Task WriteScriptAsync(Process process, string scriptText, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(scriptText.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // ssh exited before reading the whole script (connection refused,
            // auth failure...): its exit code and stderr tell the story.
        }
    }

    private static void Publish(string? line, bool isError, Action<ScriptOutputLine> onOutput)
    {
        // A null line is the end-of-stream marker, not output.
        if (line is null)
            return;

        onOutput(new ScriptOutputLine(line, isError));
    }

    /// <summary>Ends the local ssh process (FR-51.4) and waits briefly so
    /// the last output events are delivered. Anything the script already
    /// detached on the server keeps running (FR-51.5).</summary>
    private async Task KillAndDrainAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the cancellation and the kill.
        }

        using var drainSource = new CancellationTokenSource(PostKillDrainTimeout);
        try
        {
            await process.WaitForExitAsync(drainSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ssh process did not exit within {Seconds}s after being killed", PostKillDrainTimeout.TotalSeconds);
        }
    }

    private void LogOutcome(string scriptName, string host, ScriptRunResult result)
    {
        var level = result.Outcome == ScriptRunOutcome.Completed && result.ExitCode == 0 ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(
            level, "Script '{Script}' on {Host} ended: {Outcome} (exit code {ExitCode}) after {Duration}",
            scriptName, host, result.Outcome, result.ExitCode?.ToString() ?? "n/a", result.Duration);
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;

namespace MutagenMon.Core.Mutagen;

/// <summary>Invokes the `mutagen sync list` process (the output text-cleanup
/// half lives in
/// <see cref="MutagenSyncListParser"/> — see its Normalize step).
///
/// Deliberate deviation from the legacy behavior: invokes `sync list
/// --long`, not plain `sync list`. The legacy called the latter, but on
/// real mutagen builds that only prints a `Conflicts: N` summary count —
/// the per-file `(alpha) .../(beta) ...` detail lines
/// <see cref="MutagenSyncListParser"/> depends on (and that FR-8's
/// conflicts section / FR-9's resolution workflow both need) only appear
/// with `--long`. Confirmed against a real conflict in production use:
/// without the flag, HasConflicts was correctly true but the conflict
/// list stayed empty, so no "Resolve conflicts" UI ever appeared.</summary>
public sealed class MutagenCliClient : IMutagenCliClient
{
    private readonly string _mutagenPath;
    private readonly ILogger<MutagenCliClient> _logger;

    public MutagenCliClient(IOptions<MutagenMonOptions> options, ILogger<MutagenCliClient> logger)
    {
        _mutagenPath = options.Value.MutagenPath;
        _logger = logger;
    }

    public async Task<string> GetSyncListRawAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _mutagenPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("sync");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--long");

        _logger.LogDebug("Invoking '{MutagenPath} sync list --long'", _mutagenPath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        _logger.LogDebug("'{MutagenPath} sync list' exited with code {ExitCode}", _mutagenPath, process.ExitCode);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{_mutagenPath} sync list' exited with code {process.ExitCode}: {stdout}{stderr}");
        }

        return stdout + stderr;
    }

    public async Task TerminateSessionAsync(string sessionName, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _mutagenPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("sync");
        psi.ArgumentList.Add("terminate");
        psi.ArgumentList.Add(sessionName);

        _logger.LogDebug("Invoking '{MutagenPath} sync terminate {SessionName}'", _mutagenPath, sessionName);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{_mutagenPath} sync terminate {sessionName}' exited with code {process.ExitCode}: {stdout}{stderr}");
        }
    }

    public async Task CreateSessionAsync(string rawCreateCommand, CancellationToken cancellationToken)
    {
        // The stored line (mutagen-create.bat, FR-1.1) is a `mutagen`-prefixed
        // Windows command line, and may itself contain double-quoted
        // arguments (e.g. `"C:\sources\appman"`) that are only meaningful
        // as *command-line syntax* — not as literal argument content.
        // Naively re-splitting on whitespace and feeding the pieces through
        // ArgumentList (which passes each entry as literal argument text, no
        // re-parsing) would hand mutagen a path with embedded quote
        // characters and silently fail the recreate half of FR-13.5. Instead,
        // strip the leading "mutagen" token and pass the remainder as a raw
        // Arguments string, letting the OS/CRT command-line parser split and
        // unquote it exactly as it would when the .bat file is run directly.
        var firstSpace = rawCreateCommand.IndexOf(' ');
        if (firstSpace < 0)
        {
            throw new InvalidOperationException($"Malformed session create command: '{rawCreateCommand}'");
        }

        var arguments = rawCreateCommand[(firstSpace + 1)..].TrimStart();

        var psi = new ProcessStartInfo
        {
            FileName = _mutagenPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogDebug("Invoking '{MutagenPath} {Args}'", _mutagenPath, arguments);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{_mutagenPath} {arguments}' exited with code {process.ExitCode}: {stdout}{stderr}");
        }
    }
}

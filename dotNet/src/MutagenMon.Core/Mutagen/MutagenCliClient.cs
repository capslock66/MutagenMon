using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;

namespace MutagenMon.Core.Mutagen;

/// <summary>Ports mutagenmonlib/remote/mutagen.py: mutagen_sync_list()'s process
/// invocation half (the text-cleanup half moved into
/// <see cref="MutagenSyncListParser"/> — see its Normalize step).</summary>
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

        _logger.LogDebug("Invoking '{MutagenPath} sync list'", _mutagenPath);

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
}

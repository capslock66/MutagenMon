using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;

namespace MutagenMon.Core.Resolution;

/// <summary>
/// Process/file-IO implementation of <see cref="IConflictFileClient"/>.
/// Passes arguments via <see cref="ProcessStartInfo.ArgumentList"/> instead
/// of building a shell command line — manually escaping spaces/parens/
/// ampersands for a shell string is a
/// class of bug avoided entirely once no local shell is involved.
/// </summary>
public sealed class ConflictFileClient : IConflictFileClient
{
    private readonly string _scpPath;
    private readonly string _sshPath;
    private readonly string _mergePath;
    private readonly ILogger<ConflictFileClient> _logger;
    private readonly string _tempDir;

    public ConflictFileClient(IOptions<MutagenMonOptions> options, ILogger<ConflictFileClient> logger)
    {
        _scpPath = options.Value.ScpPath;
        _sshPath = options.Value.SshPath;
        _mergePath = options.Value.MergePath;
        _logger = logger;
        _tempDir = Path.Combine(Path.GetTempPath(), "MutagenMon", "conflict-cache");
        Directory.CreateDirectory(_tempDir);
    }

    public async Task<FileStat> StatAsync(SessionEndpoint endpoint, string relativePath, CancellationToken cancellationToken)
    {
        if (endpoint.Transport == TransportKind.Local)
        {
            var fileInfo = new FileInfo(JoinPath(endpoint.Url, relativePath));
            return new FileStat(fileInfo.Length, new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));
        }

        var remotePath = JoinPath(endpoint.RemoteDirectory!, relativePath);
        var output = await RunSshAsync(endpoint.Server!, $"stat -c '%Y %s' '{remotePath}'", cancellationToken);
        var parts = output.Trim().Split(' ', 2);
        var modifiedUnixSeconds = long.Parse(parts[0]);
        var sizeBytes = long.Parse(parts[1]);
        return new FileStat(sizeBytes, DateTimeOffset.FromUnixTimeSeconds(modifiedUnixSeconds));
    }

    public async Task CopyBetweenEndpointsAsync(
        SessionEndpoint source, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken)
    {
        if (source.Transport == TransportKind.Local && destination.Transport == TransportKind.Local)
        {
            File.Copy(JoinPath(source.Url, relativePath), JoinPath(destination.Url, relativePath), overwrite: true);
            return;
        }

        if (source.Transport == TransportKind.Ssh && destination.Transport == TransportKind.Ssh)
        {
            var tempFile = Path.Combine(_tempDir, "temp");
            await RunScpAsync(RemoteArg(source, relativePath), tempFile, cancellationToken);
            await RunScpAsync(tempFile, RemoteArg(destination, relativePath), cancellationToken);
            return;
        }

        var sourceArg = source.Transport == TransportKind.Ssh ? RemoteArg(source, relativePath) : JoinPath(source.Url, relativePath);
        var destinationArg = destination.Transport == TransportKind.Ssh ? RemoteArg(destination, relativePath) : JoinPath(destination.Url, relativePath);
        await RunScpAsync(sourceArg, destinationArg, cancellationToken);
    }

    public async Task<string> FetchLocalCopyAsync(SessionEndpoint endpoint, string relativePath, int side, CancellationToken cancellationToken)
    {
        if (endpoint.Transport == TransportKind.Local)
        {
            return JoinPath(endpoint.Url, relativePath);
        }

        var localPath = Path.Combine(_tempDir, $"remote{side}");
        _logger.LogInformation("Downloading '{RelativePath}' from {Endpoint} to local working copy {LocalPath}", relativePath, endpoint.Url, localPath);
        await RunScpAsync(RemoteArg(endpoint, relativePath), localPath, cancellationToken);
        _logger.LogInformation("Download complete: {Endpoint} -> {LocalPath}", endpoint.Url, localPath);
        return localPath;
    }

    public async Task PushLocalFileAsync(string localPath, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken)
    {
        if (destination.Transport == TransportKind.Ssh)
        {
            _logger.LogInformation("Uploading local working copy {LocalPath} to {Endpoint} ('{RelativePath}')", localPath, destination.Url, relativePath);
            await RunScpAsync(localPath, RemoteArg(destination, relativePath), cancellationToken);
            _logger.LogInformation("Upload complete: {LocalPath} -> {Endpoint}", localPath, destination.Url);
        }
        else
        {
            _logger.LogInformation("Copying local working copy {LocalPath} to local endpoint {Endpoint} ('{RelativePath}')", localPath, destination.Url, relativePath);
            File.Copy(localPath, JoinPath(destination.Url, relativePath), overwrite: true);
        }
    }

    public async Task RunMergeToolAsync(string localPath1, string localPath2, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _mergePath,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.ArgumentList.Add(localPath1);
        psi.ArgumentList.Add(localPath2);

        _logger.LogInformation("Visual merge tool launching: '{MergePath}' comparing {Path1} vs {Path2}", _mergePath, localPath1, localPath2);

        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.WaitForExitAsync(cancellationToken);

        _logger.LogInformation("Visual merge tool closed (exit code {ExitCode})", process.ExitCode);
    }

    private async Task<string> RunSshAsync(string server, string remoteCommand, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _sshPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(server);
        psi.ArgumentList.Add(remoteCommand);

        _logger.LogDebug("Invoking '{SshPath} {Server} {Command}'", _sshPath, server, remoteCommand);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{_sshPath} {server} {remoteCommand}' exited with code {process.ExitCode}: {stdout}{stderr}");
        }

        return stdout;
    }

    private async Task RunScpAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _scpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(destination);

        _logger.LogDebug("Invoking '{ScpPath} {Source} {Destination}'", _scpPath, source, destination);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{_scpPath} {source} {destination}' exited with code {process.ExitCode}: {stdout}{stderr}");
        }
    }

    private static string RemoteArg(SessionEndpoint endpoint, string relativePath) =>
        $"{endpoint.Server}:{JoinPath(endpoint.RemoteDirectory!, relativePath)}";

    /// <summary>Joins a
    /// directory and a relative file name with exactly one '/', regardless of
    /// whether either side already has one.</summary>
    private static string JoinPath(string directory, string relativePath)
    {
        var dir = directory.EndsWith('/') ? directory[..^1] : directory;
        var name = relativePath.StartsWith('/') ? relativePath[1..] : relativePath;
        return dir + "/" + name;
    }
}

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
            var path = JoinPath(endpoint.Url, relativePath);
            if (Directory.Exists(path))
            {
                return new FileStat(0, new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero), IsDirectory: true);
            }
            if (!File.Exists(path))
            {
                return new FileStat(0, DateTimeOffset.MinValue, Exists: false);
            }
            var fileInfo = new FileInfo(path);
            return new FileStat(fileInfo.Length, new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));
        }

        // Single round trip: reports directory/file/missing together with
        // the stat info, rather than probing the kind first and stat-ing
        // separately (twice the SSH latency for the common file case).
        var remotePath = JoinPath(endpoint.RemoteDirectory!, relativePath);
        var output = (await RunSshAsync(
            endpoint.Server!,
            $"if [ -d '{remotePath}' ]; then echo DIR $(stat -c '%Y' '{remotePath}'); " +
            $"elif [ -e '{remotePath}' ]; then echo FILE $(stat -c '%Y %s' '{remotePath}'); " +
            $"else echo MISSING; fi",
            cancellationToken)).Trim();

        var tokens = output.Split(' ');
        return tokens[0] switch
        {
            "DIR" => new FileStat(0, DateTimeOffset.FromUnixTimeSeconds(long.Parse(tokens[1])), IsDirectory: true),
            "FILE" => new FileStat(long.Parse(tokens[2]), DateTimeOffset.FromUnixTimeSeconds(long.Parse(tokens[1]))),
            _ => new FileStat(0, DateTimeOffset.MinValue, Exists: false),
        };
    }

    public async Task CopyBetweenEndpointsAsync(
        SessionEndpoint source, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken)
    {
        var sourceKind = await GetKindAsync(source, relativePath, cancellationToken);

        if (sourceKind == PathKind.Missing)
        {
            // The source side no longer has this entry (e.g. it was
            // deleted) — mirroring that absence onto the destination IS
            // the resolution.
            await DeleteAsync(destination, relativePath, cancellationToken);
            return;
        }

        if (sourceKind == PathKind.Directory)
        {
            // Directory-level conflict: replace whatever is currently on
            // the destination with an exact copy of the source subtree.
            await DeleteAsync(destination, relativePath, cancellationToken);
            await CopyDirectoryBetweenEndpointsAsync(source, destination, relativePath, cancellationToken);
            return;
        }

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

    private async Task CopyDirectoryBetweenEndpointsAsync(
        SessionEndpoint source, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken)
    {
        if (source.Transport == TransportKind.Local && destination.Transport == TransportKind.Local)
        {
            CopyLocalDirectoryRecursive(JoinPath(source.Url, relativePath), JoinPath(destination.Url, relativePath));
            return;
        }

        if (source.Transport == TransportKind.Ssh && destination.Transport == TransportKind.Ssh)
        {
            var tempDir = Path.Combine(_tempDir, "temp");
            DeleteLocalPathIfExists(tempDir);
            await RunScpAsync(RemoteArg(source, relativePath), tempDir, cancellationToken, recursive: true);
            await RunScpAsync(tempDir, RemoteArg(destination, relativePath), cancellationToken, recursive: true);
            return;
        }

        var sourceArg = source.Transport == TransportKind.Ssh ? RemoteArg(source, relativePath) : JoinPath(source.Url, relativePath);
        var destinationArg = destination.Transport == TransportKind.Ssh ? RemoteArg(destination, relativePath) : JoinPath(destination.Url, relativePath);
        await RunScpAsync(sourceArg, destinationArg, cancellationToken, recursive: true);
    }

    private enum PathKind { Missing, File, Directory }

    private async Task<PathKind> GetKindAsync(SessionEndpoint endpoint, string relativePath, CancellationToken cancellationToken)
    {
        if (endpoint.Transport == TransportKind.Local)
        {
            var path = JoinPath(endpoint.Url, relativePath);
            if (Directory.Exists(path)) return PathKind.Directory;
            return File.Exists(path) ? PathKind.File : PathKind.Missing;
        }

        var remotePath = JoinPath(endpoint.RemoteDirectory!, relativePath);
        var output = (await RunSshAsync(
            endpoint.Server!,
            $"if [ -d '{remotePath}' ]; then echo DIR; elif [ -e '{remotePath}' ]; then echo FILE; else echo MISSING; fi",
            cancellationToken)).Trim();
        return output switch
        {
            "DIR" => PathKind.Directory,
            "FILE" => PathKind.File,
            _ => PathKind.Missing,
        };
    }

    private async Task DeleteAsync(SessionEndpoint endpoint, string relativePath, CancellationToken cancellationToken)
    {
        if (endpoint.Transport == TransportKind.Local)
        {
            DeleteLocalPathIfExists(JoinPath(endpoint.Url, relativePath));
            return;
        }

        await RunSshAsync(endpoint.Server!, $"rm -rf '{JoinPath(endpoint.RemoteDirectory!, relativePath)}'", cancellationToken);
    }

    private static void CopyLocalDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            File.Copy(filePath, Path.Combine(destinationDir, Path.GetFileName(filePath)), overwrite: true);
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyLocalDirectoryRecursive(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    private static void DeleteLocalPathIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
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

    private async Task RunScpAsync(string source, string destination, CancellationToken cancellationToken, bool recursive = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _scpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (recursive)
        {
            psi.ArgumentList.Add("-r");
        }
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

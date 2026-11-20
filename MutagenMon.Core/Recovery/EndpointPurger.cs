using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Ssh;

namespace MutagenMon.Core.Recovery;

/// <summary>What <see cref="EndpointPurger.PurgeAsync"/> emptied: the absolute root whose content was deleted.</summary>
public sealed record PurgeResult(string ResolvedRoot);

/// <summary>
/// Steps 1 to 3 of the recovery sequence (FR-59): resolves the root of an
/// endpoint, refuses a filesystem root, then deletes everything *inside* it —
/// never the root itself. Works on a local folder or on a POSIX SSH server;
/// anything else is not supported (FR-59.1).
///
/// Deliberately not sealed, with <see cref="RunSshAsync"/> virtual, so tests
/// substitute the ssh call with a subclass (NFR-11). The local half is
/// exercised on real temporary folders.
/// </summary>
public class EndpointPurger
{
    private readonly string _sshPath;
    private readonly TimeSpan _timeout;
    private readonly ILogger<EndpointPurger> _logger;

    public EndpointPurger(IOptions<MutagenMonOptions> options, ILogger<EndpointPurger> logger)
    {
        _sshPath = options.Value.SshPath;
        _timeout = TimeSpan.FromSeconds(options.Value.ScriptTimeoutSeconds);
        _logger = logger;
    }

    /// <summary>FR-59.1: a local folder, or an SSH endpoint of the form
    /// <c>[user@]host:path</c>. The poll parser classifies every "x:y" URL as
    /// SSH, including Mutagen's other transports (<c>docker://container/path</c>),
    /// which are refused here.</summary>
    public static bool IsSupported(SessionEndpoint endpoint, out string reason)
    {
        reason = "";
        if (endpoint.Transport == TransportKind.Local)
            return true;

        if (endpoint.Url.Contains("://", StringComparison.Ordinal))
        {
            reason = $"'{endpoint.Url}' does not use the SSH transport";
            return false;
        }

        if (string.IsNullOrWhiteSpace(endpoint.Server))
        {
            reason = $"'{endpoint.Url}' has no SSH host";
            return false;
        }

        return true;
    }

    /// <summary>Resolves <paramref name="endpoint"/>'s root, applies the path
    /// guard and deletes the content of the root (FR-59 steps 1 to 3). Bounded
    /// by <c>ScriptTimeoutSeconds</c> (a value of 0 or less means no timeout,
    /// like for the scripts). Throws when the endpoint is not supported, the
    /// root is refused, or any step fails.</summary>
    public virtual async Task<PurgeResult> PurgeAsync(SessionEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (!IsSupported(endpoint, out var reason))
            throw new NotSupportedException($"Cannot purge: {reason}.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout > TimeSpan.Zero)
            timeout.CancelAfter(_timeout);
        try
        {
            // The local deletion runs on a pool thread: called from the manual action it must not freeze the UI thread.
            return endpoint.Transport == TransportKind.Local
                ? await Task.Run(() => PurgeLocal(endpoint.Url, timeout.Token), timeout.Token)
                : await PurgeSshAsync(endpoint, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The purge of '{endpoint.Url}' did not finish within {_timeout.TotalSeconds:0} seconds.");
        }
    }

    private PurgeResult PurgeLocal(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Refused: the local root path is empty.");

        // The guard looks at the real folder: a junction or symbolic link that points at a filesystem root must not pass.
        var root = ResolveLocalRoot(Path.GetFullPath(url));
        if (IsFileSystemRoot(root))
            throw new InvalidOperationException($"Refused: '{url}' is a filesystem root.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The local folder '{root}' does not exist.");

        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEntry(entry, cancellationToken);
            removed++;
        }

        _logger.LogInformation("Purged local folder '{Root}': {Removed} entr(y/ies) removed", root, removed);
        return new PurgeResult(root);
    }

    private async Task<PurgeResult> PurgeSshAsync(SessionEndpoint endpoint, CancellationToken cancellationToken)
    {
        var path = endpoint.RemoteDirectory ?? "";
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Refused: the remote root of '{endpoint.Url}' is empty.");

        var server = endpoint.Server!;

        // Step 1: the absolute, symlink-free path on the server (a relative path is relative to the SSH home).
        var resolvedOutput = await RunSshAsync(server, $"cd -P -- {RemotePathArgument(path)} && pwd -P", cancellationToken);
        // Only the "\r" that the forced terminal adds (-tt) is removed: spaces and tabs at the end of a line belong to a
        // folder name ("sync " is not "sync"), and deleting inside the wrong folder is not an option.
        var root = resolvedOutput.Split('\n').Select(l_ => l_.TrimEnd('\r')).LastOrDefault(l_ => !string.IsNullOrWhiteSpace(l_)) ?? "";
        if (!root.StartsWith('/'))
            throw new InvalidOperationException(
                $"'{endpoint.Url}' did not resolve to an absolute POSIX path (got '{root}'): is it a POSIX server?");

        // Step 2: the path guard.
        if (root == "/")
            throw new InvalidOperationException($"Refused: '{endpoint.Url}' resolves to the filesystem root.");

        // Step 3: delete what is inside the root, never the root itself. -xdev: do not cross into other filesystems.
        // Note: a mount point below the root makes find fail on it (exit code != 0): the purge is then partial and the
        // reset is not run — safe for the data, but the session stays halted until the mount point is dealt with.
        await RunSshAsync(server, $"find {ShellQuoting.Quote(root)} -mindepth 1 -xdev -delete", cancellationToken);
        _logger.LogInformation("Purged '{Server}:{Root}' over SSH", server, root);
        return new PurgeResult(root);
    }

    /// <summary>The final target when <paramref name="fullPath"/> is a symbolic
    /// link or a junction, otherwise the path itself.</summary>
    private static string ResolveLocalRoot(string fullPath)
    {
        try
        {
            return new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (IOException)
        {
            return fullPath; // not a link, or a path that does not exist (reported later)
        }
    }

    /// <summary>True for an empty path or a root such as <c>/</c>, <c>C:\</c>
    /// or a UNC share root.</summary>
    internal static bool IsFileSystemRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        return string.IsNullOrEmpty(root)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The remote path as one shell word. A leading <c>~</c> stays
    /// unquoted so the remote shell still expands it; everything else is
    /// quoted (<see cref="ShellQuoting.Quote"/>).</summary>
    internal static string RemotePathArgument(string path)
    {
        if (path == "~")
            return "~";
        return path.StartsWith("~/", StringComparison.Ordinal) ? "~/" + ShellQuoting.Quote(path[2..]) : ShellQuoting.Quote(path);
    }

    private static void DeleteEntry(string path, CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(path); // a directory symlink or junction: removed as a link, never followed
            else
                DeleteDirectoryTree(path, attributes, cancellationToken);
            return;
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    /// <summary>Deletes a real directory entry by entry (not with a recursive
    /// <c>Directory.Delete</c>), so the cancellation is honoured inside a big
    /// sub-folder, links are never followed and read-only files or folders
    /// do not block it.</summary>
    private static void DeleteDirectoryTree(string path, FileAttributes attributes, CancellationToken cancellationToken)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEntry(child, cancellationToken);
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        Directory.Delete(path);
    }

    /// <summary>Runs one remote command through ssh and returns its standard
    /// output; throws when ssh exits with a non-zero code. The same
    /// invocation as the conflict file operations: <c>--</c> ends option
    /// parsing, so a host value cannot be read as an ssh option. A terminal is
    /// forced (<c>-tt</c>) on purpose: without one, killing the local ssh
    /// process only closes the channel, and a remote <c>find -delete</c> — which
    /// writes nothing — would never notice and would go on deleting; with
    /// one, the disconnection hangs the remote process up (SIGHUP). When the
    /// call is cancelled (timeout included) the ssh process is killed.</summary>
    protected virtual async Task<string> RunSshAsync(string server, string remoteCommand, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _sshPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-tt");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(server);
        psi.ArgumentList.Add(remoteCommand);

        _logger.LogDebug("Invoking '{SshPath} {Server} {Command}'", _sshPath, server, remoteCommand);

        using var process = new Process { StartInfo = psi };
        process.Start();
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"'{_sshPath} {server} {remoteCommand}' exited with code {process.ExitCode}: {stdout}{stderr}");

            return stdout;
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone: nothing left to stop.
        }
    }
}

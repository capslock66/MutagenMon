using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Resolution;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ConflictResolutionServiceTests
{
    private sealed class FakeConflictFileClient : IConflictFileClient
    {
        public readonly List<(SessionEndpoint Source, SessionEndpoint Destination, string RelativePath)> Copies = new();
        public readonly List<(string LocalPath, SessionEndpoint Destination, string RelativePath)> Pushes = new();
        public bool TouchLocalPath1DuringMerge;
        public bool TouchLocalPath2DuringMerge;
        public string? LocalCopy1;
        public string? LocalCopy2;

        public Task<FileStat> StatAsync(SessionEndpoint endpoint, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new FileStat(1, DateTimeOffset.UtcNow));

        public Task CopyBetweenEndpointsAsync(SessionEndpoint source, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken)
        {
            Copies.Add((source, destination, relativePath));
            return Task.CompletedTask;
        }

        public Task<string> FetchLocalCopyAsync(SessionEndpoint endpoint, string relativePath, int side, CancellationToken cancellationToken)
        {
            var path = side == 1 ? LocalCopy1! : LocalCopy2!;
            return Task.FromResult(path);
        }

        public Task PushLocalFileAsync(string localPath, SessionEndpoint destination, string relativePath, CancellationToken cancellationToken)
        {
            Pushes.Add((localPath, destination, relativePath));
            return Task.CompletedTask;
        }

        public Task RunMergeToolAsync(string localPath1, string localPath2, CancellationToken cancellationToken)
        {
            if (TouchLocalPath1DuringMerge)
                File.SetLastWriteTimeUtc(localPath1, DateTime.UtcNow.AddSeconds(5));
            if (TouchLocalPath2DuringMerge)
                File.SetLastWriteTimeUtc(localPath2, DateTime.UtcNow.AddSeconds(5));
            return Task.CompletedTask;
        }
    }

    private static readonly SessionEndpoint LocalAlpha = new("C:/local/alpha-sync", TransportKind.Local, null, null);
    private static readonly SessionEndpoint SshBeta = new("remote:/home/alpha-sync", TransportKind.Ssh, "remote", "/home/alpha-sync");
    private static readonly PendingConflict Conflict = new("alpha-sync", "shared.txt", LocalAlpha, SshBeta);

    private static string NewTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "file.txt");
        File.WriteAllText(path, "content");
        return path;
    }

    private static (ConflictResolutionService Service, FakeConflictFileClient Client, ResolveLogWriter Log, string LogDir) Build()
    {
        var client = new FakeConflictFileClient();
        var logDir = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        var log = new ResolveLogWriter(logDir);
        return (new ConflictResolutionService(client, log), client, log, logDir);
    }

    [Fact]
    public async Task ResolveAWinsCopiesAlphaOverBetaAndLogs()
    {
        var (service, client, _, logDir) = Build();

        await service.ResolveAsync(Conflict, ConflictResolutionChoice.AWins, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(client.Copies);
        Assert.Equal(LocalAlpha, client.Copies[0].Source);
        Assert.Equal(SshBeta, client.Copies[0].Destination);
        Assert.Contains("A wins", File.ReadAllText(Path.Combine(logDir, "resolve.log")));
    }

    [Fact]
    public async Task ResolveBWinsCopiesBetaOverAlphaAndLogs()
    {
        var (service, client, _, logDir) = Build();

        await service.ResolveAsync(Conflict, ConflictResolutionChoice.BWins, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(client.Copies);
        Assert.Equal(SshBeta, client.Copies[0].Source);
        Assert.Equal(LocalAlpha, client.Copies[0].Destination);
        Assert.Contains("B wins", File.ReadAllText(Path.Combine(logDir, "resolve.log")));
    }

    [Fact]
    public async Task CompleteVisualMergeReturnsFalseAndDoesNotPropagateWhenNothingChanged()
    {
        var (service, client, _, logDir) = Build();
        client.LocalCopy1 = NewTempFile();
        client.LocalCopy2 = NewTempFile();
        client.TouchLocalPath1DuringMerge = false;

        var preparation = await service.PrepareVisualMergeAsync(Conflict, CancellationToken.None);
        await service.RunMergeToolAsync(preparation, CancellationToken.None);
        var result = await service.CompleteVisualMergeAsync(Conflict, preparation, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(client.Pushes);
        Assert.False(File.Exists(Path.Combine(logDir, "resolve.log")));
    }

    [Fact]
    public async Task CompleteVisualMergePushesToBothSidesAndLogsWhenTheToolChangedAlphaLocalCopy()
    {
        var (service, client, _, logDir) = Build();
        client.LocalCopy1 = NewTempFile();
        client.LocalCopy2 = NewTempFile();
        client.TouchLocalPath1DuringMerge = true;

        var preparation = await service.PrepareVisualMergeAsync(Conflict, CancellationToken.None);
        await service.RunMergeToolAsync(preparation, CancellationToken.None);
        var result = await service.CompleteVisualMergeAsync(Conflict, preparation, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(result);
        // Alpha is local: the local copy IS the real file already, so no push back to alpha.
        Assert.Single(client.Pushes);
        Assert.Equal(SshBeta, client.Pushes[0].Destination);
        Assert.Contains("Visual merge", File.ReadAllText(Path.Combine(logDir, "resolve.log")));
    }

    [Fact]
    public async Task CompleteVisualMergeDetectsAndPushesWhenTheToolChangedTheBetaLocalCopyInstead()
    {
        // Reported scenario: alpha is local, beta is SSH (a downloaded temp
        // copy, e.g. WinMerge's right pane) — the user edits/saves the
        // BETA side instead of alpha. Before the fix, only alpha's mtime
        // was checked, so this looked like "nothing changed" and silently
        // dropped the edit.
        var (service, client, _, logDir) = Build();
        client.LocalCopy1 = NewTempFile();
        client.LocalCopy2 = NewTempFile();
        client.TouchLocalPath2DuringMerge = true;

        var preparation = await service.PrepareVisualMergeAsync(Conflict, CancellationToken.None);
        await service.RunMergeToolAsync(preparation, CancellationToken.None);
        var result = await service.CompleteVisualMergeAsync(Conflict, preparation, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(result);
        // Beta (SSH) changed: push back up to Beta itself (its local copy
        // was only a downloaded temp file) AND to Alpha (the other side).
        Assert.Equal(2, client.Pushes.Count);
        Assert.Contains(client.Pushes, p => p.Destination == SshBeta && p.LocalPath == client.LocalCopy2);
        Assert.Contains(client.Pushes, p => p.Destination == LocalAlpha && p.LocalPath == client.LocalCopy2);
        Assert.Contains("Visual merge", File.ReadAllText(Path.Combine(logDir, "resolve.log")));
    }

    [Fact]
    public async Task CompleteVisualMergePrefersAlphaAsTieBreakWhenBothSidesChanged()
    {
        var (service, client, _, _) = Build();
        client.LocalCopy1 = NewTempFile();
        client.LocalCopy2 = NewTempFile();
        client.TouchLocalPath1DuringMerge = true;
        client.TouchLocalPath2DuringMerge = true;

        var preparation = await service.PrepareVisualMergeAsync(Conflict, CancellationToken.None);
        await service.RunMergeToolAsync(preparation, CancellationToken.None);
        await service.CompleteVisualMergeAsync(Conflict, preparation, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.All(client.Pushes, p => Assert.Equal(client.LocalCopy1, p.LocalPath));
    }

    [Fact]
    public async Task CompleteVisualMergePushesBackToAlphaTooWhenAlphaIsRemote()
    {
        var conflict = new PendingConflict("alpha-sync", "shared.txt", SshBeta, LocalAlpha);
        var (service, client, _, _) = Build();
        client.LocalCopy1 = NewTempFile();
        client.LocalCopy2 = NewTempFile();
        client.TouchLocalPath1DuringMerge = true;

        var preparation = await service.PrepareVisualMergeAsync(conflict, CancellationToken.None);
        await service.RunMergeToolAsync(preparation, CancellationToken.None);
        await service.CompleteVisualMergeAsync(conflict, preparation, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(2, client.Pushes.Count);
        Assert.Contains(client.Pushes, p => p.Destination == SshBeta);
        Assert.Contains(client.Pushes, p => p.Destination == LocalAlpha);
    }
}

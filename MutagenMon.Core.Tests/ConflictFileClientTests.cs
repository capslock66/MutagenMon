using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Resolution;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>Covers the local-filesystem branches only (directory-level
/// conflicts, FR-9) — the SSH branches shell out to real ssh/scp processes
/// and are not testable without one available on the test machine.</summary>
public class ConflictFileClientTests
{
    private static ConflictFileClient NewClient() =>
        new(Options.Create(new MutagenMonOptions()), NullLogger<ConflictFileClient>.Instance);

    private static string NewSyncRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SessionEndpoint LocalEndpoint(string root) => new(root, TransportKind.Local, null, null);

    [Fact]
    public async Task StatAsyncReturnsIsDirectoryForALocalDirectory()
    {
        var root = NewSyncRoot();
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        var stat = await NewClient().StatAsync(LocalEndpoint(root), "sub", CancellationToken.None);

        Assert.True(stat.IsDirectory);
        Assert.True(stat.Exists);
        Assert.Equal(0, stat.SizeBytes);
    }

    [Fact]
    public async Task StatAsyncReturnsNotExistsForAMissingLocalEntry()
    {
        var root = NewSyncRoot();

        var stat = await NewClient().StatAsync(LocalEndpoint(root), "gone", CancellationToken.None);

        Assert.False(stat.Exists);
        Assert.False(stat.IsDirectory);
    }

    [Fact]
    public async Task StatAsyncReturnsFileInfoForARegularFileUnchanged()
    {
        var root = NewSyncRoot();
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");

        var stat = await NewClient().StatAsync(LocalEndpoint(root), "a.txt", CancellationToken.None);

        Assert.True(stat.Exists);
        Assert.False(stat.IsDirectory);
        Assert.Equal(5, stat.SizeBytes);
    }

    [Fact]
    public async Task CopyBetweenEndpointsMirrorsADirectoryRecursively()
    {
        var sourceRoot = NewSyncRoot();
        var destinationRoot = NewSyncRoot();
        Directory.CreateDirectory(Path.Combine(sourceRoot, "dir", "nested"));
        File.WriteAllText(Path.Combine(sourceRoot, "dir", "top.txt"), "top");
        File.WriteAllText(Path.Combine(sourceRoot, "dir", "nested", "deep.txt"), "deep");
        // Stray content on the destination that must be replaced, not merged with.
        Directory.CreateDirectory(Path.Combine(destinationRoot, "dir"));
        File.WriteAllText(Path.Combine(destinationRoot, "dir", "stale.txt"), "stale");

        await NewClient().CopyBetweenEndpointsAsync(
            LocalEndpoint(sourceRoot), LocalEndpoint(destinationRoot), "dir", CancellationToken.None);

        Assert.Equal("top", File.ReadAllText(Path.Combine(destinationRoot, "dir", "top.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(destinationRoot, "dir", "nested", "deep.txt")));
        Assert.False(File.Exists(Path.Combine(destinationRoot, "dir", "stale.txt")));
    }

    [Fact]
    public async Task CopyBetweenEndpointsDeletesTheDestinationWhenTheSourceNoLongerExists()
    {
        var sourceRoot = NewSyncRoot();
        var destinationRoot = NewSyncRoot();
        Directory.CreateDirectory(Path.Combine(destinationRoot, "dir"));
        File.WriteAllText(Path.Combine(destinationRoot, "dir", "stale.txt"), "stale");

        await NewClient().CopyBetweenEndpointsAsync(
            LocalEndpoint(sourceRoot), LocalEndpoint(destinationRoot), "dir", CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "dir")));
    }

    [Fact]
    public async Task CopyBetweenEndpointsStillCopiesARegularFileUnchanged()
    {
        var sourceRoot = NewSyncRoot();
        var destinationRoot = NewSyncRoot();
        File.WriteAllText(Path.Combine(sourceRoot, "a.txt"), "content");
        File.WriteAllText(Path.Combine(destinationRoot, "a.txt"), "old");

        await NewClient().CopyBetweenEndpointsAsync(
            LocalEndpoint(sourceRoot), LocalEndpoint(destinationRoot), "a.txt", CancellationToken.None);

        Assert.Equal("content", File.ReadAllText(Path.Combine(destinationRoot, "a.txt")));
    }

    [Fact]
    public void BuildDeleteCommandQuotesThePathAndEndsOptionParsing()
    {
        var command = ConflictFileClient.BuildDeleteCommand("/data/a'; touch x; '.txt");

        Assert.Equal("rm -rf -- '/data/a'\\''; touch x; '\\''.txt'", command);
    }

    [Fact]
    public void BuildDeleteCommandDoesNotLetADashNamedFileBecomeAnOption()
    {
        Assert.Equal("rm -rf -- '/data/-rf'", ConflictFileClient.BuildDeleteCommand("/data/-rf"));
    }

    [Fact]
    public void BuildStatAndKindCommandsQuoteEveryOccurrenceOfThePath()
    {
        const string remotePath = "/data/it's $(id).txt";
        const string quoted = "'/data/it'\\''s $(id).txt'";

        var statCommand = ConflictFileClient.BuildStatCommand(remotePath);
        var kindCommand = ConflictFileClient.BuildKindCommand(remotePath);

        Assert.Equal(4, statCommand.Split(quoted).Length - 1);
        Assert.Equal(2, kindCommand.Split(quoted).Length - 1);
        Assert.DoesNotContain("'" + remotePath + "'", statCommand);
        Assert.DoesNotContain("'" + remotePath + "'", kindCommand);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("docs/../../etc")]
    [InlineData("..\\secrets.txt")]
    public void EnsureSafeRelativePathRejectsRootAndTraversal(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => ConflictFileClient.EnsureSafeRelativePath(relativePath));
    }

    [Theory]
    [InlineData("notes..v2.txt")]
    [InlineData("..hidden")]
    [InlineData("sub/it's.txt")]
    public void EnsureSafeRelativePathAcceptsDotsInsideNames(string relativePath)
    {
        Assert.Null(Record.Exception(() => ConflictFileClient.EnsureSafeRelativePath(relativePath)));
    }

    [Fact]
    public async Task CopyBetweenEndpointsRefusesToReplaceTheEndpointRootDirectory()
    {
        var sourceRoot = NewSyncRoot();
        var destinationRoot = NewSyncRoot();
        File.WriteAllText(Path.Combine(destinationRoot, "keep.txt"), "data");

        await Assert.ThrowsAsync<ArgumentException>(() => NewClient().CopyBetweenEndpointsAsync(
            LocalEndpoint(sourceRoot), LocalEndpoint(destinationRoot), "", CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(destinationRoot, "keep.txt")));
    }

    [Fact]
    public async Task CopyBetweenEndpointsRefusesToMirrorAMissingRootOntoTheDestinationRoot()
    {
        var sourceRoot = Path.Combine(NewSyncRoot(), "missing");
        var destinationRoot = NewSyncRoot();
        File.WriteAllText(Path.Combine(destinationRoot, "keep.txt"), "data");

        await Assert.ThrowsAsync<ArgumentException>(() => NewClient().CopyBetweenEndpointsAsync(
            LocalEndpoint(sourceRoot), LocalEndpoint(destinationRoot), "", CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(destinationRoot, "keep.txt")));
    }
}

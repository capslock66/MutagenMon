using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Recovery;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-59 steps 1 to 3: root resolution, path guard and purge of the content of an endpoint.</summary>
public class EndpointPurgerTests
{
    private sealed class FakeSshPurger(int timeoutSeconds = 300)
        : EndpointPurger(Options.Create(new MutagenMonOptions { ScriptTimeoutSeconds = timeoutSeconds }), NullLogger<EndpointPurger>.Instance)
    {
        public readonly List<(string Server, string Command)> Calls = new();
        public Func<string, string> Responder = _ => "";
        public bool NeverReturns;

        protected override async Task<string> RunSshAsync(string server, string remoteCommand, CancellationToken cancellationToken)
        {
            Calls.Add((server, remoteCommand));
            if (NeverReturns)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return Responder(remoteCommand);
        }
    }

    private static SessionEndpoint Ssh(string url) =>
        new(url, TransportKind.Ssh, url[..url.IndexOf(':')], url[(url.IndexOf(':') + 1)..]);

    private static SessionEndpoint Local(string path) => new(path, TransportKind.Local, null, null);

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutagenmon-purge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------------------------------------------------------------- support (FR-59.1)

    [Fact]
    public void ALocalFolderAndASshEndpointAreSupported()
    {
        Assert.True(EndpointPurger.IsSupported(Local(@"C:\temp"), out _));
        Assert.True(EndpointPurger.IsSupported(Ssh("robbie:sources/temp"), out _));
    }

    [Fact]
    public void ADockerEndpointIsNotSupported()
    {
        var endpoint = new SessionEndpoint("docker://container/path", TransportKind.Ssh, "docker", "//container/path");

        Assert.False(EndpointPurger.IsSupported(endpoint, out var reason));
        Assert.Contains("SSH", reason);
    }

    [Fact]
    public async Task PurgingAnUnsupportedEndpointThrowsBeforeAnyCall()
    {
        var purger = new FakeSshPurger();
        var endpoint = new SessionEndpoint("docker://c/p", TransportKind.Ssh, "docker", "//c/p");

        await Assert.ThrowsAsync<NotSupportedException>(() => purger.PurgeAsync(endpoint, CancellationToken.None));

        Assert.Empty(purger.Calls);
    }

    // ---------------------------------------------------------------- SSH

    [Fact]
    public async Task ASshPurgeResolvesTheRootThenDeletesItsContentOnly()
    {
        var purger = new FakeSshPurger { Responder = command => command.StartsWith("cd ") ? "/home/user/sources/temp\n" : "" };

        var result = await purger.PurgeAsync(Ssh("robbie:sources/temp"), CancellationToken.None);

        Assert.Equal("/home/user/sources/temp", result.ResolvedRoot);
        Assert.Equal(
            [
                ("robbie", "cd -P -- 'sources/temp' && pwd -P"),
                ("robbie", "find '/home/user/sources/temp' -mindepth 1 -xdev -delete"),
            ],
            purger.Calls);
    }

    [Fact]
    public async Task TheRemotePathIsQuotedAsOneShellWord()
    {
        var purger = new FakeSshPurger { Responder = command => command.StartsWith("cd ") ? "/home/u/it's here\n" : "" };

        await purger.PurgeAsync(Ssh("robbie:it's here"), CancellationToken.None);

        Assert.Equal("cd -P -- 'it'\\''s here' && pwd -P", purger.Calls[0].Command);
        Assert.Equal("find '/home/u/it'\\''s here' -mindepth 1 -xdev -delete", purger.Calls[1].Command);
    }

    [Fact]
    public void ALeadingTildeStaysUnquotedSoTheRemoteShellExpandsIt()
    {
        Assert.Equal("~", EndpointPurger.RemotePathArgument("~"));
        Assert.Equal("~/'my dir'", EndpointPurger.RemotePathArgument("~/my dir"));
        Assert.Equal("'sources/x'", EndpointPurger.RemotePathArgument("sources/x"));
    }

    [Fact]
    public async Task ARemoteRootThatResolvesToTheFilesystemRootIsRefusedAndNothingIsDeleted()
    {
        var purger = new FakeSshPurger { Responder = _ => "/\n" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => purger.PurgeAsync(Ssh("robbie:/"), CancellationToken.None));

        Assert.Contains("filesystem root", ex.Message);
        Assert.Single(purger.Calls);
        Assert.DoesNotContain(purger.Calls, c => c.Command.StartsWith("find"));
    }

    [Fact]
    public async Task ARelativeRemotePathThatResolvesToTheRootIsAlsoRefused()
    {
        var purger = new FakeSshPurger { Responder = _ => "/\n" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => purger.PurgeAsync(Ssh("robbie:../.."), CancellationToken.None));

        Assert.DoesNotContain(purger.Calls, c => c.Command.StartsWith("find"));
    }

    [Fact]
    public async Task AOneLevelFolderAndAHomeAreAllowed()
    {
        var purger = new FakeSshPurger { Responder = command => command.StartsWith("cd ") ? "/media\n" : "" };

        var result = await purger.PurgeAsync(Ssh("robbie:/media"), CancellationToken.None);

        Assert.Equal("/media", result.ResolvedRoot);
        Assert.Contains(purger.Calls, c => c.Command == "find '/media' -mindepth 1 -xdev -delete");
    }

    [Fact]
    public async Task AnEmptyRemotePathIsRefused()
    {
        var purger = new FakeSshPurger();

        await Assert.ThrowsAsync<InvalidOperationException>(() => purger.PurgeAsync(Ssh("robbie:"), CancellationToken.None));

        Assert.Empty(purger.Calls);
    }

    [Fact]
    public async Task ANonAbsoluteAnswerMeansItIsNotAPosixServer()
    {
        var purger = new FakeSshPurger { Responder = _ => "C:\\Users\\me\n" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => purger.PurgeAsync(Ssh("winbox:sources/x"), CancellationToken.None));

        Assert.Contains("POSIX", ex.Message);
        Assert.DoesNotContain(purger.Calls, c => c.Command.StartsWith("find"));
    }

    [Fact]
    public async Task TheLastLineOfTheAnswerIsTheRoot()
    {
        // A login banner printed before the path must not be taken for it.
        var purger = new FakeSshPurger { Responder = command => command.StartsWith("cd ") ? "Welcome!\n/home/u/x\n" : "" };

        var result = await purger.PurgeAsync(Ssh("robbie:x"), CancellationToken.None);

        Assert.Equal("/home/u/x", result.ResolvedRoot);
    }

    [Fact]
    public async Task OnlyTheCarriageReturnOfAForcedTerminalIsRemovedFromTheResolvedRoot()
    {
        // With a pty (-tt) every line ends with "\r\n"; a trailing space belongs to the folder name ("sync ").
        var purger = new FakeSshPurger { Responder = command => command.StartsWith("cd ") ? "/home/u/sync \r\n" : "" };

        var result = await purger.PurgeAsync(Ssh("robbie:sync "), CancellationToken.None);

        Assert.Equal("/home/u/sync ", result.ResolvedRoot);
        Assert.Contains(purger.Calls, c => c.Command == "find '/home/u/sync ' -mindepth 1 -xdev -delete");
    }

    [Fact]
    public async Task ACarriageReturnOnTheResolvedRootIsStripped()
    {
        var purger = new FakeSshPurger { Responder = command => command.StartsWith("cd ") ? "/home/u/x\r\n" : "" };

        var result = await purger.PurgeAsync(Ssh("robbie:x"), CancellationToken.None);

        Assert.Equal("/home/u/x", result.ResolvedRoot);
    }

    [Fact]
    public async Task ASshFailureIsPropagated()
    {
        var purger = new FakeSshPurger { Responder = _ => throw new InvalidOperationException("ssh exited with code 255") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => purger.PurgeAsync(Ssh("robbie:x"), CancellationToken.None));
    }

    [Fact]
    public async Task APurgeThatTakesTooLongTimesOut()
    {
        var purger = new FakeSshPurger(timeoutSeconds: 1) { NeverReturns = true };

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => purger.PurgeAsync(Ssh("robbie:x"), CancellationToken.None));

        Assert.Contains("1 seconds", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ATimeoutOfZeroOrLessMeansNoTimeoutLikeForTheScripts(int timeoutSeconds)
    {
        var purger = new FakeSshPurger(timeoutSeconds) { Responder = c => c.StartsWith("cd ") ? "/home/u/x\n" : "" };

        var result = await purger.PurgeAsync(Ssh("robbie:x"), CancellationToken.None);

        Assert.Equal("/home/u/x", result.ResolvedRoot);
    }

    [Fact]
    public async Task ACallerCancellationIsNotReportedAsATimeout()
    {
        var purger = new FakeSshPurger { NeverReturns = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => purger.PurgeAsync(Ssh("robbie:x"), cancellation.Token));
    }

    // ---------------------------------------------------------------- local

    [Fact]
    public async Task ALocalPurgeDeletesEverythingInsideButKeepsTheRoot()
    {
        var root = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, ".hidden"), "h");
            Directory.CreateDirectory(Path.Combine(root, "sub", "deeper"));
            File.WriteAllText(Path.Combine(root, "sub", "deeper", "b.txt"), "b");
            var readOnly = Path.Combine(root, "sub", "readonly.txt");
            File.WriteAllText(readOnly, "r");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            var result = await new FakeSshPurger().PurgeAsync(Local(root), CancellationToken.None);

            Assert.True(Directory.Exists(root));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Equal(Path.GetFullPath(root), result.ResolvedRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ALocalPurgeOfAnEmptyFolderIsFine()
    {
        var root = NewTempDirectory();
        try
        {
            await new FakeSshPurger().PurgeAsync(Local(root), CancellationToken.None);

            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task ALocalPurgeNeverFollowsADirectorySymbolicLink()
    {
        if (!OperatingSystem.IsLinux())
            return; // creating a symbolic link needs a privilege on Windows

        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

            await new FakeSshPurger().PurgeAsync(Local(root), CancellationToken.None);

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ACancelledLocalPurgeStopsBeforeDeletingAnything()
    {
        var root = NewTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "big", "deeper"));
            File.WriteAllText(Path.Combine(root, "big", "deeper", "a.txt"), "a");
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FakeSshPurger().PurgeAsync(Local(root), cancelled.Token));

            Assert.True(File.Exists(Path.Combine(root, "big", "deeper", "a.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ALocalRootThatIsASymbolicLinkIsResolvedBeforeThePurge()
    {
        if (!OperatingSystem.IsLinux())
            return; // creating a symbolic link needs a privilege on Windows

        var target = NewTempDirectory();
        var holder = NewTempDirectory();
        var link = Path.Combine(holder, "rootlink");
        try
        {
            File.WriteAllText(Path.Combine(target, "a.txt"), "a");
            Directory.CreateSymbolicLink(link, target);

            var result = await new FakeSshPurger().PurgeAsync(Local(link), CancellationToken.None);

            Assert.Equal(Path.GetFullPath(target), result.ResolvedRoot);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.True(Directory.Exists(link)); // the link itself is not removed
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task ALocalRootThatIsASymbolicLinkToTheFilesystemRootIsRefused()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var holder = NewTempDirectory();
        try
        {
            var link = Path.Combine(holder, "toroot");
            Directory.CreateSymbolicLink(link, "/");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new FakeSshPurger().PurgeAsync(Local(link), CancellationToken.None));

            Assert.Contains("filesystem root", ex.Message);
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingLocalFolderIsAnError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mutagenmon-missing-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new FakeSshPurger().PurgeAsync(Local(missing), CancellationToken.None));
    }

    [Fact]
    public async Task ALocalFilesystemRootIsRefused()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new FakeSshPurger().PurgeAsync(Local(root), CancellationToken.None));

        Assert.Contains("filesystem root", ex.Message);
    }

    [Fact]
    public async Task AnEmptyLocalPathIsRefused() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FakeSshPurger().PurgeAsync(Local("  "), CancellationToken.None));

    [Fact]
    public void FilesystemRootsAreRecognised()
    {
        Assert.True(EndpointPurger.IsFileSystemRoot(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.False(EndpointPurger.IsFileSystemRoot(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)));
    }
}

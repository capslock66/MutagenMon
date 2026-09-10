using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SessionEditingServiceTests
{
    private sealed class FakeMutagenCliClient : IMutagenCliClient
    {
        public readonly List<string> TerminatedSessions = new();
        public readonly List<string> CreatedRawCommands = new();
        public string? FailTerminationFor;
        public string? FailCreationContaining;

        public Task<string> GetSyncListRawAsync(CancellationToken cancellationToken) => Task.FromResult("");

        public Task TerminateSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            if (sessionName == FailTerminationFor)
                throw new InvalidOperationException($"cannot terminate '{sessionName}'");
            TerminatedSessions.Add(sessionName);
            return Task.CompletedTask;
        }

        public Task CreateSessionAsync(string rawCreateCommand, CancellationToken cancellationToken)
        {
            if (FailCreationContaining is { } needle && rawCreateCommand.Contains(needle))
                throw new InvalidOperationException($"cannot create '{rawCreateCommand}'");
            CreatedRawCommands.Add(rawCreateCommand);
            return Task.CompletedTask;
        }
    }

    private static string CreateTempSessionsFile(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutagenmon-editing-test-{Guid.NewGuid():N}.bat");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public async Task AddCreatesTheSessionLiveThenAppendsItsLine()
    {
        var path = CreateTempSessionsFile("@echo off");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "new-session", Alpha = "A", Beta = "B" };

            await service.AddAsync(model, CancellationToken.None);

            Assert.Single(cli.CreatedRawCommands);
            Assert.Contains("--name=new-session", cli.CreatedRawCommands[0]);
            Assert.Equal(["@echo off", "mutagen sync create A B --name=new-session"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddDoesNotTouchTheFileWhenCreationFails()
    {
        var path = CreateTempSessionsFile("@echo off");
        try
        {
            var cli = new FakeMutagenCliClient { FailCreationContaining = "new-session" };
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "new-session", Alpha = "A", Beta = "B" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(model, CancellationToken.None));

            Assert.Equal(["@echo off"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditTerminatesUnderTheOldNameThenRecreatesAndReplacesInPlace()
    {
        var path = CreateTempSessionsFile(
            "@echo off",
            "mutagen sync create --name=before A B",
            "mutagen sync create --name=other X Y");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "after", Alpha = "A", Beta = "B", Mode = SyncMode.OneWaySafe };

            await service.EditAsync("before", model, CancellationToken.None);

            Assert.Equal(["before"], cli.TerminatedSessions);
            Assert.Contains("--name=after", cli.CreatedRawCommands[0]);
            Assert.Equal(
                ["@echo off", "mutagen sync create A B --name=after -m one-way-safe", "mutagen sync create --name=other X Y"],
                File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditToleratesTerminationFailureAndStillRecreates()
    {
        var path = CreateTempSessionsFile("mutagen sync create --name=before A B");
        try
        {
            var cli = new FakeMutagenCliClient { FailTerminationFor = "before" };
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "before", Alpha = "A2", Beta = "B2" };

            await service.EditAsync("before", model, CancellationToken.None);

            Assert.Empty(cli.TerminatedSessions);
            Assert.Single(cli.CreatedRawCommands);
            Assert.Equal(["mutagen sync create A2 B2 --name=before"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditDoesNotTouchTheFileWhenRecreationFails()
    {
        var path = CreateTempSessionsFile("mutagen sync create --name=before A B");
        try
        {
            var cli = new FakeMutagenCliClient { FailCreationContaining = "after" };
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "after", Alpha = "A", Beta = "B" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EditAsync("before", model, CancellationToken.None));

            Assert.Equal(["mutagen sync create --name=before A B"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeleteTerminatesThenRemovesTheLine()
    {
        var path = CreateTempSessionsFile(
            "mutagen sync create --name=a A B",
            "mutagen sync create --name=b X Y");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());

            await service.DeleteAsync("a", CancellationToken.None);

            Assert.Equal(["a"], cli.TerminatedSessions);
            Assert.Equal(["mutagen sync create --name=b X Y"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeleteToleratesTerminationFailureAndStillRemovesTheLine()
    {
        var path = CreateTempSessionsFile("mutagen sync create --name=a A B");
        try
        {
            var cli = new FakeMutagenCliClient { FailTerminationFor = "a" };
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());

            await service.DeleteAsync("a", CancellationToken.None);

            Assert.Empty(cli.TerminatedSessions);
            Assert.Empty(File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeletePropagatesWhenTheLineCannotBeRemoved()
    {
        var path = CreateTempSessionsFile("mutagen sync create --name=other X Y");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("missing", CancellationToken.None));

            Assert.Equal(["mutagen sync create --name=other X Y"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

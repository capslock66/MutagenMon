using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Mutagen;
using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SessionEditingServiceTests
{
    private sealed class FakeMutagenCliClient()
        : MutagenCliClient(Options.Create(new MutagenMonOptions()), NullLogger<MutagenCliClient>.Instance)
    {
        public readonly List<string> TerminatedSessions = new();
        public readonly List<string> CreatedRawCommands = new();
        public string? FailTerminationFor;
        public string? FailCreationContaining;

        public override Task<string> GetSyncListRawAsync(CancellationToken cancellationToken) => Task.FromResult("");

        public override Task<string> GetSyncStatusDetailAsync(string sessionName, CancellationToken cancellationToken) => Task.FromResult("");

        public override Task TerminateSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            if (sessionName == FailTerminationFor)
                throw new InvalidOperationException($"cannot terminate '{sessionName}'");
            TerminatedSessions.Add(sessionName);
            return Task.CompletedTask;
        }

        public override Task CreateSessionAsync(string rawCreateCommand, CancellationToken cancellationToken)
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

    private const string DirectiveForX = "rem sync --name=x : @halted-auto-fix: true";

    [Fact]
    public async Task AddWritesTheDirectiveAfterCreatingTheSession()
    {
        var path = CreateTempSessionsFile("@echo off", "", "mutagen sync terminate --all");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "x", Alpha = "A", Beta = "B", HaltedAutoFix = true };

            await service.AddAsync(model, CancellationToken.None);

            Assert.Equal(
                ["@echo off", DirectiveForX, "", "mutagen sync terminate --all", "mutagen sync create A B --name=x"],
                File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddWithInheritRemovesAnOrphanDirectiveOfTheSameName()
    {
        // The session line of "x" was deleted by hand, its directive stayed (FR-64.5). Session "y" has its own directive.
        const string directiveForY = "rem sync --name=y : @halted-auto-fix: false";
        var path = CreateTempSessionsFile(
            "@echo off", DirectiveForX, directiveForY, "", "mutagen sync terminate --all", "mutagen sync create A B --name=y");
        try
        {
            var service = new SessionEditingService(new FakeMutagenCliClient(), path, new CapturingLogger<SessionEditingService>());

            await service.AddAsync(new SessionCommandLine { Name = "x", Alpha = "A", Beta = "B" }, CancellationToken.None);

            Assert.Equal(
                [
                    "@echo off", directiveForY, "", "mutagen sync terminate --all",
                    "mutagen sync create A B --name=y", "mutagen sync create A B --name=x",
                ],
                File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddWithInheritWritesNoDirective()
    {
        var path = CreateTempSessionsFile("@echo off");
        try
        {
            var service = new SessionEditingService(new FakeMutagenCliClient(), path, new CapturingLogger<SessionEditingService>());

            await service.AddAsync(new SessionCommandLine { Name = "x", Alpha = "A", Beta = "B" }, CancellationToken.None);

            Assert.Equal(["@echo off", "mutagen sync create A B --name=x"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnEditThatOnlyChangesTheDirectiveDoesNotTerminateOrRecreateTheSession()
    {
        var path = CreateTempSessionsFile("@echo off", "", "mutagen sync terminate --all", "mutagen sync create A B --name=x");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "x", Alpha = "A", Beta = "B", HaltedAutoFix = true };

            var outcome = await service.EditAsync("x", model, CancellationToken.None);

            Assert.Equal(SessionEditOutcome.DirectiveOnly, outcome);
            Assert.Empty(cli.TerminatedSessions);
            Assert.Empty(cli.CreatedRawCommands);
            Assert.Equal(
                ["@echo off", DirectiveForX, "", "mutagen sync terminate --all", "mutagen sync create A B --name=x"],
                File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AHandWrittenLineThatIsEquivalentOnceNormalisedCountsAsUnchanged()
    {
        // Same command as the model below, but with the flags in another order and the short mode flag.
        const string handWritten = "mutagen sync create --name=x -m two-way-safe A B";
        var path = CreateTempSessionsFile(handWritten);
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = SessionCommandLineParser.Parse(handWritten);
            model.HaltedAutoFix = false;

            var outcome = await service.EditAsync("x", model, CancellationToken.None);

            Assert.Equal(SessionEditOutcome.DirectiveOnly, outcome);
            Assert.Empty(cli.CreatedRawCommands);
            Assert.Contains("rem sync --name=x : @halted-auto-fix: false", File.ReadAllLines(path));
            Assert.Contains(handWritten, File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnEditWithNothingChangedLeavesTheFileAlone()
    {
        var path = CreateTempSessionsFile("@echo off", "mutagen sync create A B --name=x");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var before = File.ReadAllText(path);

            var outcome = await service.EditAsync(
                "x", new SessionCommandLine { Name = "x", Alpha = "A", Beta = "B" }, CancellationToken.None);

            Assert.Equal(SessionEditOutcome.DirectiveOnly, outcome);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnEditThatRenamesTheSessionRecreatesItAndRenamesItsDirective()
    {
        var path = CreateTempSessionsFile(
            "@echo off", DirectiveForX, "", "mutagen sync terminate --all", "mutagen sync create A B --name=x");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "y", Alpha = "A", Beta = "B", HaltedAutoFix = true };

            var outcome = await service.EditAsync("x", model, CancellationToken.None);

            Assert.Equal(SessionEditOutcome.Recreated, outcome);
            Assert.Equal(["x"], cli.TerminatedSessions);
            Assert.Equal(
                ["@echo off", "rem sync --name=y : @halted-auto-fix: true", "", "mutagen sync terminate --all", "mutagen sync create A B --name=y"],
                File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnEditThatChangesTheCommandAndSetsInheritRemovesTheDirective()
    {
        var path = CreateTempSessionsFile("@echo off", DirectiveForX, "", "mutagen sync create A B --name=x");
        try
        {
            var cli = new FakeMutagenCliClient();
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var model = new SessionCommandLine { Name = "x", Alpha = "A", Beta = "C" };

            var outcome = await service.EditAsync("x", model, CancellationToken.None);

            Assert.Equal(SessionEditOutcome.Recreated, outcome);
            Assert.Equal(["@echo off", "", "mutagen sync create A C --name=x"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFailedEditLeavesTheFileAndItsDirectiveUntouched()
    {
        var path = CreateTempSessionsFile("@echo off", DirectiveForX, "", "mutagen sync create A B --name=x");
        try
        {
            var cli = new FakeMutagenCliClient { FailCreationContaining = "--name=x" };
            var service = new SessionEditingService(cli, path, new CapturingLogger<SessionEditingService>());
            var before = File.ReadAllLines(path);
            var model = new SessionCommandLine { Name = "x", Alpha = "A", Beta = "C", HaltedAutoFix = false };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EditAsync("x", model, CancellationToken.None));

            Assert.Equal(before, File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeleteAlsoRemovesTheSessionDirective()
    {
        var path = CreateTempSessionsFile(
            "@echo off", DirectiveForX, "", "mutagen sync terminate --all", "mutagen sync create A B --name=x");
        try
        {
            var service = new SessionEditingService(new FakeMutagenCliClient(), path, new CapturingLogger<SessionEditingService>());

            await service.DeleteAsync("x", CancellationToken.None);

            Assert.Equal(["@echo off", "", "mutagen sync terminate --all"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

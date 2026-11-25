using MutagenMon.Core.Updates;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-66.2/FR-66.3: update detection by last write time of MutagenMon.exe.</summary>
public class UpdateCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mutagenmon-update-test-{Guid.NewGuid():N}");

    public UpdateCheckerTests() => Directory.CreateDirectory(Path.Combine(_root, "deploy"));

    public void Dispose() => Directory.Delete(_root, true);

    private string CreateExe(string relativeDir, DateTime writeTimeUtc)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, UpdateChecker.ExeName);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
        return path;
    }

    [Fact]
    public void ADeployedExeNewerThanTheRunningOneIsAnUpdate()
    {
        var running = CreateExe("install", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CreateExe("deploy", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var result = UpdateChecker.Check(Path.Combine(_root, "deploy"), running, _root);

        Assert.True(result.IsAvailable);
        Assert.Equal(Path.Combine(_root, "deploy", UpdateChecker.ExeName), result.DeployedExePath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ASameOrOlderDeployedExeIsNotAnUpdate(int daysOffset)
    {
        var current = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var running = CreateExe("install", current);
        CreateExe("deploy", current.AddDays(daysOffset));

        Assert.False(UpdateChecker.Check(Path.Combine(_root, "deploy"), running, _root).IsAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyDeployPathDisablesTheCheck(string? deployPath)
    {
        var running = CreateExe("install", DateTime.UtcNow);

        Assert.Equal(UpdateCheckResult.None, UpdateChecker.Check(deployPath, running, _root));
    }

    [Fact]
    public void AMissingFolderOrExeIsNotAnUpdateAndDoesNotThrow()
    {
        var running = CreateExe("install", DateTime.UtcNow);

        Assert.False(UpdateChecker.Check(Path.Combine(_root, "nowhere"), running, _root).IsAvailable);
        Assert.False(UpdateChecker.Check(Path.Combine(_root, "deploy"), running, _root).IsAvailable);
    }

    [Fact]
    public void AMissingFolderOrExeReportsAReason()
    {
        var running = CreateExe("install", DateTime.UtcNow);

        Assert.Contains("not found", UpdateChecker.Check(Path.Combine(_root, "nowhere"), running, _root).FailureReason);
        Assert.Contains(UpdateChecker.ExeName, UpdateChecker.Check(Path.Combine(_root, "deploy"), running, _root).FailureReason);
        Assert.Null(UpdateChecker.Check("", running, _root).FailureReason);
        Assert.Contains("running executable", UpdateChecker.Check(Path.Combine(_root, "deploy"), null, _root).FailureReason);
    }

    [Fact]
    public void ARelativeDeployPathIsResolvedAgainstTheBaseDirectory()
    {
        var running = CreateExe("install", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CreateExe("deploy", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(UpdateChecker.Check("deploy", running, _root).IsAvailable);
    }

    [Fact]
    public void EnvironmentVariablesInTheDeployPathAreExpanded()
    {
        var running = CreateExe("install", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CreateExe("deploy", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        const string variable = "MUTAGENMON_TEST_DEPLOY_ROOT";
        Environment.SetEnvironmentVariable(variable, _root);
        try
        {
            Assert.True(UpdateChecker.Check($"%{variable}%{Path.DirectorySeparatorChar}deploy", running, _root).IsAvailable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void AMissingRunningExeReportsAReason()
    {
        CreateExe("deploy", DateTime.UtcNow);

        Assert.Contains("running executable", UpdateChecker.Check(Path.Combine(_root, "deploy"), Path.Combine(_root, "none.exe"), _root).FailureReason);
    }
}

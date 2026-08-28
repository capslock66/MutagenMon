using MutagenMon.Core.Resolution;
using Xunit;

namespace MutagenMon.Core.Tests;

public class ResolveLogWriterTests
{
    [Fact]
    public void AppendWritesOneEntryWithAllFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        var writer = new ResolveLogWriter(dir);
        var timestamp = new DateTimeOffset(2026, 8, 21, 10, 30, 0, TimeSpan.Zero);

        writer.Append("alpha-sync", "C:/local/alpha-sync", "remote:/home/alpha-sync", "shared.txt", "A wins", automatic: false, timestamp);

        var content = File.ReadAllText(Path.Combine(dir, "resolve.log"));
        Assert.Contains("[2026-08-21 10:30:00]", content);
        Assert.Contains("alpha-sync", content);
        Assert.Contains("C:/local/alpha-sync", content);
        Assert.Contains("remote:/home/alpha-sync", content);
        Assert.Contains("shared.txt", content);
        Assert.Contains("A wins", content);
        Assert.DoesNotContain("[AUTO]", content);
    }

    [Fact]
    public void AppendMarksAutomaticResolutions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        var writer = new ResolveLogWriter(dir);

        writer.Append("alpha-sync", "a", "b", "file.txt", "B wins", automatic: true, DateTimeOffset.UtcNow);

        var content = File.ReadAllText(Path.Combine(dir, "resolve.log"));
        Assert.Contains("[AUTO]", content);
    }

    [Fact]
    public void AppendAccumulatesMultipleEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MutagenMon.Tests", Guid.NewGuid().ToString("N"));
        var writer = new ResolveLogWriter(dir);

        writer.Append("alpha-sync", "a", "b", "one.txt", "A wins", automatic: false, DateTimeOffset.UtcNow);
        writer.Append("alpha-sync", "a", "b", "two.txt", "B wins", automatic: false, DateTimeOffset.UtcNow);

        var content = File.ReadAllText(Path.Combine(dir, "resolve.log"));
        Assert.Contains("one.txt", content);
        Assert.Contains("two.txt", content);
    }
}

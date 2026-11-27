using Xunit;
using MutagenMon.Core.Configuration;
using MutagenMon.Core.Logging;

namespace MutagenMon.Core.Tests;

public sealed class LogFilePurgerTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 27, 10, 0, 0);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LogFilePurgerTests-" + Guid.NewGuid().ToString("N"));
    private readonly string _logPath;

    public LogFilePurgerTests()
    {
        Directory.CreateDirectory(_directory);
        _logPath = Path.Combine(_directory, "mutagenMon.log");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Entry(string date, string message) =>
        $"{date} 08:15:42.123 +01:00 [INF] Cat: {message}";

    [Fact]
    public void Default_retention_is_30_days()
    {
        Assert.Equal(30, new MutagenMonOptions().LogRetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_retention_falls_back_to_30_days(int configured)
    {
        Assert.Equal(30, LogFilePurger.NormalizeRetentionDays(configured));
    }

    [Fact]
    public void Missing_file_removes_nothing()
    {
        Assert.Equal(0, LogFilePurger.PurgeOlderThan(_logPath, 30, Today));
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void Entries_older_than_retention_are_removed_and_recent_ones_kept()
    {
        // 2026-11-27 minus 30 days = 2026-10-28: that day is kept, the day before is not.
        File.WriteAllLines(_logPath,
        [
            Entry("2026-10-01", "too old"),
            Entry("2026-10-27", "just too old"),
            Entry("2026-10-28", "oldest kept"),
            Entry("2026-11-27", "today"),
        ]);

        var removed = LogFilePurger.PurgeOlderThan(_logPath, 30, Today);

        Assert.Equal(2, removed);
        Assert.Equal(
            [Entry("2026-10-28", "oldest kept"), Entry("2026-11-27", "today")],
            File.ReadAllLines(_logPath));
    }

    [Fact]
    public void Continuation_lines_follow_their_entry()
    {
        File.WriteAllLines(_logPath,
        [
            Entry("2026-09-01", "old failure"),
            "System.InvalidOperationException: old stack trace",
            "   at Foo.Bar()",
            Entry("2026-11-26", "recent failure"),
            "System.InvalidOperationException: recent stack trace",
        ]);

        var removed = LogFilePurger.PurgeOlderThan(_logPath, 30, Today);

        Assert.Equal(3, removed);
        Assert.Equal(
            [Entry("2026-11-26", "recent failure"), "System.InvalidOperationException: recent stack trace"],
            File.ReadAllLines(_logPath));
    }

    [Fact]
    public void Nothing_old_leaves_the_file_untouched()
    {
        File.WriteAllLines(_logPath, [Entry("2026-11-20", "a"), Entry("2026-11-27", "b")]);
        var before = File.GetLastWriteTimeUtc(_logPath);

        var removed = LogFilePurger.PurgeOlderThan(_logPath, 30, Today);

        Assert.Equal(0, removed);
        Assert.Equal(before, File.GetLastWriteTimeUtc(_logPath));
        Assert.Equal(2, File.ReadAllLines(_logPath).Length);
    }

    [Fact]
    public void Everything_old_empties_the_file()
    {
        File.WriteAllLines(_logPath, [Entry("2026-01-01", "a"), Entry("2026-01-02", "b")]);

        var removed = LogFilePurger.PurgeOlderThan(_logPath, 30, Today);

        Assert.Equal(2, removed);
        Assert.Empty(File.ReadAllLines(_logPath));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        File.WriteAllLines(_logPath, [Entry("2026-01-01", "a"), Entry("2026-11-27", "b")]);

        LogFilePurger.PurgeOlderThan(_logPath, 30, Today);

        Assert.Equal([_logPath], Directory.GetFiles(_directory));
    }

    [Fact]
    public void Empty_file_removes_nothing()
    {
        File.WriteAllText(_logPath, "");

        Assert.Equal(0, LogFilePurger.PurgeOlderThan(_logPath, 30, Today));
    }

    [Fact]
    public void Lines_before_the_first_timestamp_are_kept()
    {
        File.WriteAllLines(_logPath, ["foreign header", Entry("2026-01-01", "old"), Entry("2026-11-27", "new")]);

        var removed = LogFilePurger.PurgeOlderThan(_logPath, 30, Today);

        Assert.Equal(1, removed);
        Assert.Equal(["foreign header", Entry("2026-11-27", "new")], File.ReadAllLines(_logPath));
    }

    [Fact]
    public void Last_line_without_trailing_newline_is_handled()
    {
        File.WriteAllText(_logPath, Entry("2026-01-01", "old") + Environment.NewLine + Entry("2026-11-27", "new"));

        var removed = LogFilePurger.PurgeOlderThan(_logPath, 30, Today);

        Assert.Equal(1, removed);
        Assert.Equal([Entry("2026-11-27", "new")], File.ReadAllLines(_logPath));
    }

    [Fact]
    public void Locked_file_is_left_intact_and_reports_failure()
    {
        // File locks are mandatory on Windows only; on Unix they are advisory and never block a reader.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Exclusive file locks are enforced on Windows only.");
        File.WriteAllLines(_logPath, [Entry("2026-01-01", "old"), Entry("2026-11-27", "new")]);
        using var lockHandle = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(-1, LogFilePurger.PurgeOlderThan(_logPath, 30, Today));
    }

    [Fact]
    public void Huge_retention_is_capped_instead_of_throwing()
    {
        File.WriteAllLines(_logPath, [Entry("2026-01-01", "a")]);

        Assert.Equal(0, LogFilePurger.PurgeOlderThan(_logPath, int.MaxValue, Today));
        Assert.Equal(LogFilePurger.MaxRetentionDays, LogFilePurger.NormalizeRetentionDays(int.MaxValue));
    }
}

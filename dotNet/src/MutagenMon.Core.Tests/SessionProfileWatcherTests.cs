using MutagenMon.Core.Mutagen;
using MutagenMon.Core.ProfileWatch;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SessionProfileWatcherTests
{
    private sealed class FakeTimestampProvider : IFileTimestampProvider
    {
        public Dictionary<string, DateTimeOffset?> Timestamps { get; } = new();
        public DateTimeOffset? GetLastWriteTimeUtc(string path) => Timestamps.GetValueOrDefault(path);
    }

    private static ParsedSessionStatus StatusWithId(string id) =>
        new("s", id, "Watching for changes", false, false, false, null, null);

    [Fact]
    public void ZeroWatchPeriodDisablesTheCheckEntirely()
    {
        var fake = new FakeTimestampProvider();
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 0);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        Assert.False(watcher.Tick(statuses));
    }

    [Fact]
    public void FirstObservationNeverReportsAnUpdateItJustEstablishesTheBaseline()
    {
        var fake = new FakeTimestampProvider();
        fake.Timestamps[Path.Combine("/profile", "archives", "abc")] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        Assert.False(watcher.Tick(statuses));
    }

    [Fact]
    public void MtimeIncreaseOnASubsequentTickIsReportedAsUpdating()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses); // establishes baseline
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(5);
        Assert.True(watcher.Tick(statuses));
    }

    [Fact]
    public void UnchangedMtimeIsNotReportedAsUpdating()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses);
        Assert.False(watcher.Tick(statuses));
    }

    [Fact]
    public void OnlyEveryNthTickIsActuallyChecked()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 3);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        Assert.False(watcher.Tick(statuses)); // tick 1 — skipped
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(5);
        Assert.False(watcher.Tick(statuses)); // tick 2 — skipped
        Assert.False(watcher.Tick(statuses)); // tick 3 — checked, but this is the baseline observation
    }

    [Fact]
    public void MissingArchiveFileIsToleratedAndResetsTheBaseline()
    {
        var fake = new FakeTimestampProvider(); // no timestamp registered -> file "does not exist"
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        Assert.False(watcher.Tick(statuses));
    }

    [Fact]
    public void NullSessionStatusIsSkippedWithoutThrowing()
    {
        var fake = new FakeTimestampProvider();
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = null };

        Assert.False(watcher.Tick(statuses));
    }

    [Fact]
    public void FirstObservationNeverConfirmsAnUpdateItJustEstablishesTheGraceBaseline()
    {
        var fake = new FakeTimestampProvider();
        fake.Timestamps[Path.Combine("/profile", "archives", "abc")] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1, graceSeconds: 4);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses);
        Assert.Empty(watcher.ConfirmedUpdatedSessions);
    }

    [Fact]
    public void AnMtimeIncreaseWithinTheGracePeriodIsNotYetConfirmed()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1, graceSeconds: 4);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses); // establishes baseline
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2); // < 4s grace
        watcher.Tick(statuses);

        Assert.Empty(watcher.ConfirmedUpdatedSessions);
    }

    [Fact]
    public void AnMtimeIncreasePastTheGracePeriodIsConfirmedForThatSessionOnly()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1, graceSeconds: 4);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses); // establishes baseline
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(5); // > 4s grace
        watcher.Tick(statuses);

        Assert.Equal(new[] { "s" }, watcher.ConfirmedUpdatedSessions);
    }

    [Fact]
    public void RapidSuccessiveWritesWithinGraceCollapseIntoASingleConfirmedUpdate()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1, graceSeconds: 4);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses); // establishes baseline
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(1);
        watcher.Tick(statuses);
        Assert.Empty(watcher.ConfirmedUpdatedSessions);
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2);
        watcher.Tick(statuses);
        Assert.Empty(watcher.ConfirmedUpdatedSessions);
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(3);
        watcher.Tick(statuses);
        Assert.Empty(watcher.ConfirmedUpdatedSessions);

        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(10); // now past grace vs baseline
        watcher.Tick(statuses);
        Assert.Equal(new[] { "s" }, watcher.ConfirmedUpdatedSessions);
    }

    [Fact]
    public void ConfirmedUpdatesAreClearedOnTheNextTickIfNothingNewHappens()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1, graceSeconds: 4);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses); // baseline
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(5);
        watcher.Tick(statuses);
        Assert.NotEmpty(watcher.ConfirmedUpdatedSessions);

        watcher.Tick(statuses); // unchanged mtime this time
        Assert.Empty(watcher.ConfirmedUpdatedSessions);
    }

    [Fact]
    public void AMissingArchiveFileResetsTheGraceBaselineToo()
    {
        var fake = new FakeTimestampProvider();
        var path = Path.Combine("/profile", "archives", "abc");
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch;
        var watcher = new SessionProfileWatcher(fake, "/profile", watchPeriodTicks: 1, graceSeconds: 4);
        var statuses = new Dictionary<string, ParsedSessionStatus?> { ["s"] = StatusWithId("abc") };

        watcher.Tick(statuses); // baseline
        fake.Timestamps.Remove(path); // archive momentarily disappears
        watcher.Tick(statuses);
        Assert.Empty(watcher.ConfirmedUpdatedSessions);

        // File reappears with a small mtime bump — must be treated as a fresh
        // baseline (no confirmed update), matching the raw-signal behavior.
        fake.Timestamps[path] = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(1);
        watcher.Tick(statuses);
        Assert.Empty(watcher.ConfirmedUpdatedSessions);
    }
}

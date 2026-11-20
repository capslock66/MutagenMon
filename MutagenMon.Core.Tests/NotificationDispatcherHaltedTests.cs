using MutagenMon.Core.Notifications;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-62 / FR-11.5: the halt notifications have no configuration toggle.</summary>
public class NotificationDispatcherHaltedTests
{
    [Fact]
    public void AHaltedNoticeIsAlwaysQueuedEvenWithEveryOtherNotificationDisabled()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: false, notifyAutoresolve: false, notifyProfileUpdate: false);

        dispatcher.NotifyHalted("temp", "Halted due to one-sided root emptying");

        var message = Assert.Single(queue.DrainAll());
        Assert.Equal("Session halted", message.Title);
        Assert.Equal("temp: Halted due to one-sided root emptying", message.Body);
    }

    [Fact]
    public void AnAutoFixFailureNoticeIsAlwaysQueued()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, false, false, false);

        dispatcher.NotifyAutoFixFailed("temp", "purge: ssh exited with code 255");

        var message = Assert.Single(queue.DrainAll());
        Assert.Equal("Automatic fix failed", message.Title);
        Assert.Equal("temp: purge: ssh exited with code 255", message.Body);
    }
}

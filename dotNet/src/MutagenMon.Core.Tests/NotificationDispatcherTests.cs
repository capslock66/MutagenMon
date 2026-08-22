using System.Linq;
using MutagenMon.Core.Notifications;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>FR-11: turning raw signals into queued notifications, gated by
/// their own NOTIFY_* toggle.</summary>
public class NotificationDispatcherTests
{
    [Fact]
    public void NewConflictsAreQueuedAsOneGroupedNotificationWhenEnabled()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: true, notifyAutoresolve: true, notifyProfileUpdate: true);

        dispatcher.NotifyNewConflicts(new[] { "alpha-sync:a.txt", "alpha-sync:b.txt" });

        var message = Assert.Single(queue.DrainAll());
        Assert.Equal("New conflicts", message.Title);
        Assert.Equal("alpha-sync:a.txt\nalpha-sync:b.txt", message.Body);
    }

    [Fact]
    public void NewConflictsAreNotQueuedWhenTheListIsEmpty()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: true, notifyAutoresolve: true, notifyProfileUpdate: true);

        dispatcher.NotifyNewConflicts(Array.Empty<string>());

        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public void NewConflictsAreNotQueuedWhenDisabled()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: false, notifyAutoresolve: true, notifyProfileUpdate: true);

        dispatcher.NotifyNewConflicts(new[] { "alpha-sync:a.txt" });

        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public void AnAutoResolveIsQueuedNamingTheSessionFileAndRuleWhenEnabled()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: true, notifyAutoresolve: true, notifyProfileUpdate: true);

        dispatcher.NotifyAutoResolved("alpha-sync", "shared.txt", "A wins");

        var message = Assert.Single(queue.DrainAll());
        Assert.Equal("Conflict auto-resolved", message.Title);
        Assert.Equal("alpha-sync:shared.txt — A wins", message.Body);
    }

    [Fact]
    public void AnAutoResolveIsNotQueuedWhenDisabled()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: true, notifyAutoresolve: false, notifyProfileUpdate: true);

        dispatcher.NotifyAutoResolved("alpha-sync", "shared.txt", "A wins");

        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public void AConfirmedProfileUpdateIsQueuedOncePerSessionWhenEnabled()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: true, notifyAutoresolve: true, notifyProfileUpdate: true);

        dispatcher.NotifyProfileUpdated(new[] { "alpha-sync", "beta-sync" });

        var messages = queue.DrainAll();
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal("Updated", m.Title));
        Assert.Equal(new[] { "alpha-sync", "beta-sync" }, messages.Select(m => m.Body));
    }

    [Fact]
    public void AConfirmedProfileUpdateIsNotQueuedWhenDisabled()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: true, notifyAutoresolve: true, notifyProfileUpdate: false);

        dispatcher.NotifyProfileUpdated(new[] { "alpha-sync" });

        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public void NoProfileUpdateIsQueuedWhenTheListIsEmpty()
    {
        var queue = new NotificationQueue();
        var dispatcher = new NotificationDispatcher(queue, notifyConflicts: true, notifyAutoresolve: true, notifyProfileUpdate: true);

        dispatcher.NotifyProfileUpdated(Array.Empty<string>());

        Assert.Empty(queue.DrainAll());
    }
}

using MutagenMon.Core.Notifications;
using Xunit;

namespace MutagenMon.Core.Tests;

/// <summary>Thread-safe handoff for FR-11 notifications: FIFO order,
/// drain-once semantics.</summary>
public class NotificationQueueTests
{
    [Fact]
    public void DrainAllReturnsMessagesInFifoOrder()
    {
        var queue = new NotificationQueue();
        queue.Enqueue(new NotificationMessage("First", "a"));
        queue.Enqueue(new NotificationMessage("Second", "b"));

        var drained = queue.DrainAll();

        Assert.Equal(new[] { "First", "Second" }, drained.Select(m => m.Title));
    }

    [Fact]
    public void DrainAllEmptiesTheQueue()
    {
        var queue = new NotificationQueue();
        queue.Enqueue(new NotificationMessage("Title", "body"));
        queue.DrainAll();

        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public void DrainAllOnAnEmptyQueueReturnsEmpty()
    {
        var queue = new NotificationQueue();

        Assert.Empty(queue.DrainAll());
    }
}

using Achicrack.Core.Notifications;

namespace Achicrack.Core.Tests;

public class NotificationQueueTests
{
    private static NotificationRequest Req(string title, string? key = null) => new()
    {
        Kind = NotificationKind.AchievementUnlocked,
        Header = "Achievement Unlocked",
        Title = title,
        DedupKey = key,
    };

    [Fact]
    public async Task Presents_in_order_one_at_a_time()
    {
        var presenter = new RecordingPresenter { Delay = TimeSpan.FromMilliseconds(40) };
        await using var queue = new NotificationQueue(presenter);
        queue.Start();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(queue.Enqueue(Req($"N{i}")));
        }

        await Wait.UntilAsync(() => presenter.Shown.Count == 5);
        Assert.Equal(["N0", "N1", "N2", "N3", "N4"], presenter.Shown.Select(r => r.Title));
        Assert.Equal(1, presenter.MaxConcurrent);
        await Wait.UntilAsync(() => queue.PendingCount == 0);
    }

    [Fact]
    public async Task Suppresses_duplicates_within_window()
    {
        var presenter = new RecordingPresenter();
        var time = new ManualTime();
        await using var queue = new NotificationQueue(presenter, new NotificationQueueOptions { DedupWindow = TimeSpan.FromSeconds(30) }, time);
        queue.Start();

        Assert.True(queue.Enqueue(Req("A", "k1")));
        Assert.False(queue.Enqueue(Req("A again", "k1")));
        Assert.True(queue.Enqueue(Req("B", "k2")));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(queue.Enqueue(Req("A later", "k1")));

        await Wait.UntilAsync(() => presenter.Shown.Count == 3);
        Assert.Equal(["A", "B", "A later"], presenter.Shown.Select(r => r.Title));
    }

    [Fact]
    public async Task Presenter_failure_does_not_stop_queue()
    {
        var presenter = new RecordingPresenter { ThrowWhen = r => r.Title == "bad" };
        await using var queue = new NotificationQueue(presenter);
        queue.Start();

        queue.Enqueue(Req("bad"));
        queue.Enqueue(Req("good"));

        await Wait.UntilAsync(() => presenter.Shown.Any(r => r.Title == "good"));
        Assert.DoesNotContain(presenter.Shown, r => r.Title == "bad");
    }

    [Fact]
    public async Task Queued_before_start_are_presented_after_start()
    {
        var presenter = new RecordingPresenter();
        await using var queue = new NotificationQueue(presenter);
        queue.Enqueue(Req("early"));
        Assert.Equal(1, queue.PendingCount);

        queue.Start();

        await Wait.UntilAsync(() => presenter.Shown.Count == 1);
    }

    [Fact]
    public async Task Burst_of_simultaneous_unlocks_is_fully_queued()
    {
        var presenter = new RecordingPresenter { Delay = TimeSpan.FromMilliseconds(5) };
        await using var queue = new NotificationQueue(presenter);
        queue.Start();

        Parallel.For(0, 20, i => queue.Enqueue(Req($"P{i}", $"key{i}")));

        await Wait.UntilAsync(() => presenter.Shown.Count == 20);
        Assert.Equal(1, presenter.MaxConcurrent);
    }
}

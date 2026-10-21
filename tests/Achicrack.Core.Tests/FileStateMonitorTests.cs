using System.Collections.Concurrent;
using Achicrack.Core.Adapters.Goldberg;
using Achicrack.Core.Models;
using Achicrack.Core.Monitoring;
using Achicrack.FakeGame;

namespace Achicrack.Core.Tests;

public class FileStateMonitorTests
{
    private static readonly FileStateMonitorOptions FastOptions = new()
    {
        Debounce = TimeSpan.FromMilliseconds(30),
        ReconcileInterval = TimeSpan.FromMilliseconds(300),
        RetryDelay = TimeSpan.FromMilliseconds(20),
        MaxReadAttempts = 3,
    };

    private sealed class Recorder
    {
        public ConcurrentQueue<(AchievementStateSnapshot Snapshot, SnapshotKind Kind)> Items { get; } = new();

        public void Handle(AchievementStateSnapshot s, SnapshotKind k) => Items.Enqueue((s, k));

        public bool HasEarned(string api) => Items.Any(i => i.Kind == SnapshotKind.Live &&
                                                           i.Snapshot.Entries.TryGetValue(api, out var e) && e.Earned);
    }

    private static (GoldbergFixture Fixture, TempDir Dir) NewFixture()
    {
        var dir = new TempDir();
        var fixture = new GoldbergFixture(dir.Combine("game"), 4000001, dir.Combine("saves")).Create(4);
        return (fixture, dir);
    }

    [Fact]
    public async Task Delivers_existing_state_as_baseline()
    {
        var (fx, dir) = NewFixture();
        using var _ = dir;
        fx.Unlock("ACH_FAKE_01");
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, FastOptions);

        await monitor.StartAsync(rec.Handle);

        var (snapshot, kind) = Assert.Single(rec.Items);
        Assert.Equal(SnapshotKind.Baseline, kind);
        Assert.True(snapshot.Entries["ACH_FAKE_01"].Earned);
    }

    [Fact]
    public async Task Detects_atomic_replacement()
    {
        var (fx, dir) = NewFixture();
        using var _ = dir;
        fx.Unlock("ACH_FAKE_01");
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, FastOptions);
        await monitor.StartAsync(rec.Handle);

        fx.Unlock("ACH_FAKE_02");

        await Wait.UntilAsync(() => rec.HasEarned("ACH_FAKE_02"));
    }

    [Fact]
    public async Task Detects_in_place_rewrite()
    {
        var (fx, dir) = NewFixture();
        using var _ = dir;
        fx.Unlock("ACH_FAKE_01");
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, FastOptions);
        await monitor.StartAsync(rec.Handle);

        fx.UnlockInPlace("ACH_FAKE_03");

        await Wait.UntilAsync(() => rec.HasEarned("ACH_FAKE_03"));
    }

    [Fact]
    public async Task Picks_up_state_file_created_after_start_even_when_folder_did_not_exist()
    {
        var (fx, dir) = NewFixture();
        using var _ = dir;
        Assert.False(Directory.Exists(Path.GetDirectoryName(fx.StateFilePath)));
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, FastOptions);
        await monitor.StartAsync(rec.Handle);
        Assert.Empty(Assert.Single(rec.Items).Snapshot.Entries);

        fx.Unlock("ACH_FAKE_01");

        await Wait.UntilAsync(() => rec.HasEarned("ACH_FAKE_01"));
    }

    [Fact]
    public async Task Truncated_write_keeps_last_good_state_and_recovers()
    {
        var (fx, dir) = NewFixture();
        using var _ = dir;
        fx.Unlock("ACH_FAKE_01");
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, FastOptions);
        await monitor.StartAsync(rec.Handle);

        fx.WriteTruncated();
        await Wait.UntilAsync(() => monitor.FailedReadCount > 0, because: "truncated file should fail to parse");
        Assert.All(rec.Items, i => Assert.True(i.Snapshot.Entries["ACH_FAKE_01"].Earned));

        fx.Unlock("ACH_FAKE_01", "ACH_FAKE_02");
        await Wait.UntilAsync(() => rec.HasEarned("ACH_FAKE_02"));
    }

    [Fact]
    public async Task Corrupt_file_at_start_yields_empty_baseline_without_throwing()
    {
        var (fx, dir) = NewFixture();
        using var _ = dir;
        fx.WriteRaw("\0\0garbage\u0001");
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, FastOptions);

        await monitor.StartAsync(rec.Handle);

        Assert.Empty(Assert.Single(rec.Items).Snapshot.Entries);
        Assert.True(monitor.FailedReadCount > 0);
    }

    [Fact]
    public async Task Reconcile_now_reads_changes_immediately()
    {
        var (fx, dir) = NewFixture();
        using var _ = dir;
        var rec = new Recorder();
        var slow = new FileStateMonitorOptions { ReconcileInterval = TimeSpan.FromHours(1), Debounce = TimeSpan.FromHours(1) };
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, slow);
        await monitor.StartAsync(rec.Handle);

        fx.Unlock("ACH_FAKE_04");
        await monitor.ReconcileNowAsync();

        Assert.True(rec.HasEarned("ACH_FAKE_04"));
    }

    [Fact]
    public async Task Merges_multiple_candidate_paths()
    {
        using var dir = new TempDir();
        var a = new GoldbergFixture(dir.Combine("game"), 4000001, dir.Combine("gse")).Create(4);
        var b = new GoldbergFixture(dir.Combine("game"), 4000001, dir.Combine("classic"));
        a.Unlock("ACH_FAKE_01");
        b.Unlock("ACH_FAKE_02");
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [a.StateFilePath, b.StateFilePath], GoldbergStateParser.Parse, FastOptions);

        await monitor.StartAsync(rec.Handle);

        var baseline = Assert.Single(rec.Items).Snapshot;
        Assert.True(baseline.Entries["ACH_FAKE_01"].Earned);
        Assert.True(baseline.Entries["ACH_FAKE_02"].Earned);
    }

    [Fact]
    public async Task Locked_file_is_retried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // exclusive share modes are only enforced on Windows
        }

        var (fx, dir) = NewFixture();
        using var _ = dir;
        fx.Unlock("ACH_FAKE_01");
        var rec = new Recorder();
        await using var monitor = new FileStateMonitor("goldberg", [fx.StateFilePath], GoldbergStateParser.Parse, FastOptions);
        await monitor.StartAsync(rec.Handle);

        fx.Unlock("ACH_FAKE_02");
        using (new FileStream(fx.StateFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Task.Delay(200);
        }

        await Wait.UntilAsync(() => rec.HasEarned("ACH_FAKE_02"));
    }
}

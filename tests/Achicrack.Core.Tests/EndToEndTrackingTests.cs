using Achicrack.Core.Library;
using Achicrack.Core.Models;
using Achicrack.Core.Notifications;
using Achicrack.Core.Tracking;
using Achicrack.FakeGame;

namespace Achicrack.Core.Tests;

/// <summary>
/// Simulated end-to-end tests: real engine wiring + fake game folder + fake process table.
/// These prove the pipeline logic, not behavior with real games or the real emulator.
/// </summary>
public class EndToEndTrackingTests
{
    private static GameRecord Register(HostFixture h, GoldbergFixture fx)
    {
        var report = h.Host.Scanner.Scan(fx.GameDirectory);
        var game = h.Host.Registration.Register(report);
        h.Host.Detection.InvalidateGames();
        return game;
    }

    private static GoldbergFixture NewGame(HostFixture h, string name, uint appId, int count = 6) =>
        new GoldbergFixture(h.Dir.Combine("games", name), appId, Path.Combine(h.AppData, "GSE Saves")).Create(count);

    [Fact]
    public async Task Launch_track_unlock_and_exit_full_flow()
    {
        using var dir = new TempDir();
        await using var h = new HostFixture(dir);
        var fx = NewGame(h, "Fake Odyssey", 4100001);
        fx.Unlock(DateTimeOffset.FromUnixTimeSeconds(1700000000), "ACH_FAKE_01"); // pre-existing unlock
        var game = Register(h, fx);
        var states = new List<TrackingState>();
        h.Host.Coordinator.SessionChanged += (_, s) => states.Add(s.State);

        // 1. Game launches → recognized → tracking active → "Started Tracking" notification
        var pid = h.Processes.Start(game.Executables[0]);
        await h.PollAsync();

        Assert.Equal([TrackingState.GameDetected, TrackingState.InitializingTracker, TrackingState.TrackingActive], states);
        Assert.Equal(game.Id, h.Host.Detection.PrimaryGameId);
        await Wait.UntilAsync(() => h.Presenter.Shown.Count == 1);
        var tracking = h.Presenter.Shown.Single();
        Assert.Equal(NotificationKind.TrackingStarted, tracking.Kind);
        Assert.Equal("Fake Odyssey", tracking.Title);
        Assert.Contains("1 / 6", tracking.Message);

        // 2. Complete catalog with locked + unlocked (pre-existing unlock recorded silently)
        var summary = AchievementListBuilder.Build(h.Host.Catalogs.GetCatalog(game), h.Host.Achievements.GetUnlocks(game.Id));
        Assert.True(summary.IsCatalogComplete);
        Assert.Equal((1, 6), (summary.Unlocked, summary.Total!.Value));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), summary.Items[0].UnlockedAtUtc);

        // 3. Game unlocks an achievement → event → DB → notification with manifest metadata
        var unlockedEvents = new List<AchievementUnlockedEvent>();
        h.Host.Engine.AchievementUnlocked += (_, e) => unlockedEvents.Add(e);
        fx.Unlock("ACH_FAKE_02");
        await Wait.UntilAsync(() => h.Presenter.Shown.Count == 2, because: "unlock notification expected");
        var unlock = h.Presenter.Shown.Last();
        Assert.Equal(NotificationKind.AchievementUnlocked, unlock.Kind);
        Assert.Equal("Fake Achievement 2", unlock.Title);
        Assert.Equal("Description of fake achievement 2.", unlock.Message);
        Assert.NotNull(unlock.IconPath);
        Assert.Single(unlockedEvents);

        summary = AchievementListBuilder.Build(h.Host.Catalogs.GetCatalog(game), h.Host.Achievements.GetUnlocks(game.Id));
        Assert.Equal(33.3, summary.CompletionPercent);
        Assert.Equal("ACH_FAKE_02", summary.LastUnlocked!.ApiName);

        // 4. Game exits → tracking stopped, last played recorded
        h.Processes.Exit(pid);
        await h.PollAsync();
        await h.PollAsync();
        Assert.Equal(TrackingState.TrackingStopped, h.Host.Coordinator.GetSession(game.Id)!.State);
        Assert.NotNull(h.Host.Games.Get(game.Id)!.LastPlayedAtUtc);
        Assert.Null(h.Host.Detection.PrimaryGameId);

        // No extra notifications on exit
        await Task.Delay(200);
        Assert.Equal(2, h.Presenter.Shown.Count);
    }

    [Fact]
    public async Task Game_with_multiple_processes_gets_one_tracking_notification()
    {
        using var dir = new TempDir();
        await using var h = new HostFixture(dir);
        var fx = NewGame(h, "Multi", 4100002);
        var game = Register(h, fx);

        h.Processes.Start(game.Executables[0]);
        h.Processes.Start(Path.Combine(fx.BinDirectory, "worker.exe"));
        await h.PollAsync();
        h.Processes.Start(game.Executables[0]);
        await h.PollAsync();

        await Task.Delay(200);
        Assert.Single(h.Presenter.Shown, r => r.Kind == NotificationKind.TrackingStarted);
    }

    [Fact]
    public async Task Unsupported_game_reports_unavailable_without_tracking_notification()
    {
        using var dir = new TempDir();
        await using var h = new HostFixture(dir);
        var fx = NewGame(h, "NoAppId", 4100003);
        File.Delete(Path.Combine(fx.BinDirectory, "steam_appid.txt"));
        var game = Register(h, fx);
        Assert.Null(game.AppId);

        h.Processes.Start(game.Executables[0]);
        await h.PollAsync();

        var session = h.Host.Coordinator.GetSession(game.Id)!;
        Assert.Equal(TrackingState.TrackingUnavailable, session.State);
        await Task.Delay(200);
        Assert.Empty(h.Presenter.Shown);
    }

    [Fact]
    public async Task Application_restart_during_active_game_does_not_duplicate()
    {
        using var dir = new TempDir();
        var processes = new FakeProcessProvider();
        GameRecord game;
        GoldbergFixture fx;
        int pid;

        await using (var first = new HostFixture(dir, processes))
        {
            fx = NewGame(first, "Restart", 4100004);
            game = Register(first, fx);
            pid = processes.Start(game.Executables[0]);
            await first.PollAsync();
            fx.Unlock("ACH_FAKE_01");
            await Wait.UntilAsync(() => first.Presenter.Shown.Any(r => r.Kind == NotificationKind.AchievementUnlocked));
        } // simulated crash/exit of Achicrack while the game keeps running

        fx.Unlock("ACH_FAKE_01", "ACH_FAKE_02"); // unlocked while Achicrack was not running

        await using var second = new HostFixture(dir, processes);
        await second.PollAsync();
        Assert.Equal(TrackingState.TrackingActive, second.Host.Coordinator.GetSession(game.Id)!.State);

        fx.Unlock("ACH_FAKE_03");
        await Wait.UntilAsync(() => second.Presenter.Shown.Any(r => r.Kind == NotificationKind.AchievementUnlocked));
        await Task.Delay(300);

        var unlockNotifications = second.Presenter.Shown.Where(r => r.Kind == NotificationKind.AchievementUnlocked).ToList();
        Assert.Equal(["ACH_FAKE_03"], unlockNotifications.Select(r => r.ApiName));
        var unlocks = second.Host.Achievements.GetUnlocks(game.Id);
        Assert.Equal(3, unlocks.Count);
        Assert.True(unlocks["ACH_FAKE_02"].IsBaseline); // recorded silently, not lost
        Assert.False(unlocks["ACH_FAKE_01"].IsBaseline);
        GC.KeepAlive(pid);
    }

    [Fact]
    public async Task Multiple_simultaneous_games_are_tracked_independently()
    {
        using var dir = new TempDir();
        await using var h = new HostFixture(dir);
        var fxA = NewGame(h, "Alpha", 4100005);
        var fxB = NewGame(h, "Beta", 4100006);
        var a = Register(h, fxA);
        var b = Register(h, fxB);

        var pidA = h.Processes.Start(a.Executables[0]);
        h.Processes.Start(b.Executables[0]);
        h.Processes.ForegroundPid = pidA;
        await h.PollAsync();

        Assert.Equal(TrackingState.TrackingActive, h.Host.Coordinator.GetSession(a.Id)!.State);
        Assert.Equal(TrackingState.TrackingActive, h.Host.Coordinator.GetSession(b.Id)!.State);
        Assert.Equal(a.Id, h.Host.Detection.PrimaryGameId);

        fxB.Unlock("ACH_FAKE_04");
        fxA.Unlock("ACH_FAKE_01");
        await Wait.UntilAsync(() => h.Presenter.Shown.Count(r => r.Kind == NotificationKind.AchievementUnlocked) == 2);

        Assert.Equal(["ACH_FAKE_01"], h.Host.Achievements.GetUnlocks(a.Id).Keys);
        Assert.Equal(["ACH_FAKE_04"], h.Host.Achievements.GetUnlocks(b.Id).Keys);

        h.Processes.Exit(pidA);
        await h.PollAsync();
        await h.PollAsync();
        Assert.Equal(TrackingState.TrackingStopped, h.Host.Coordinator.GetSession(a.Id)!.State);
        Assert.Equal(TrackingState.TrackingActive, h.Host.Coordinator.GetSession(b.Id)!.State);
        Assert.Equal(b.Id, h.Host.Detection.PrimaryGameId);
    }

    [Fact]
    public async Task Unlock_written_right_before_exit_is_still_caught()
    {
        using var dir = new TempDir();
        await using var h = new HostFixture(dir);
        h.Settings.Update(s => s with { Monitoring = s.Monitoring with { ReconcileSeconds = 300, DebounceMilliseconds = 5000 } });
        var fx = NewGame(h, "LastSecond", 4100007);
        var game = Register(h, fx);
        var pid = h.Processes.Start(game.Executables[0]);
        await h.PollAsync();

        fx.Unlock("ACH_FAKE_06");
        h.Processes.Exit(pid);
        await h.PollAsync();
        await h.PollAsync();

        Assert.True(h.Host.Achievements.GetUnlocks(game.Id).ContainsKey("ACH_FAKE_06"));
        await Wait.UntilAsync(() => h.Presenter.Shown.Any(r => r.ApiName == "ACH_FAKE_06"));
    }

    [Fact]
    public async Task Simulated_demo_unlock_goes_through_full_pipeline()
    {
        using var dir = new TempDir();
        await using var h = new HostFixture(dir);

        var api = await h.Host.SimulateDemoUnlockAsync();

        Assert.Equal("DEMO_FIRST_STEPS", api);
        await Wait.UntilAsync(() => h.Presenter.Shown.Any(r => r.Kind == NotificationKind.AchievementUnlocked));
        Assert.Equal("First Steps", h.Presenter.Shown.Last().Title);
        Assert.Equal(RarityTier.Common, h.Presenter.Shown.Last().Rarity);
        Assert.Equal("DEMO_COLLECTOR", await h.Host.SimulateDemoUnlockAsync());
    }

    [Fact]
    public async Task Notifications_can_be_disabled_in_settings()
    {
        using var dir = new TempDir();
        await using var h = new HostFixture(dir);
        h.Settings.Update(s => s with { ShowTrackingStartedNotification = false, ShowAchievementNotification = false });
        var fx = NewGame(h, "Quiet", 4100008);
        var game = Register(h, fx);
        h.Processes.Start(game.Executables[0]);
        await h.PollAsync();
        fx.Unlock("ACH_FAKE_01");
        await Wait.UntilAsync(() => h.Host.Achievements.GetUnlocks(game.Id).Count == 1);
        await Task.Delay(200);
        Assert.Empty(h.Presenter.Shown);
    }
}

using Achicrack.Core.Events;
using Achicrack.Core.Models;
using Achicrack.Core.Persistence;

namespace Achicrack.Core.Tests;

public class EventEngineTests
{
    private static GameRecord AddGame(AchicrackDatabase db, string name = "Game")
    {
        var game = new GameRecord { Name = name, InstallDirectory = "/games/" + name, AdapterId = "goldberg", AppId = 10 };
        new GameRepository(db).Add(game);
        return game;
    }

    private static AchievementStateSnapshot Snapshot(params (string Api, bool Earned, long? Time)[] entries) =>
        new(entries.ToDictionary(e => e.Api,
                e => new AchievementStateEntry(e.Api, e.Earned, e.Time is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null)),
            []);

    [Fact]
    public void Baseline_unlocks_are_persisted_silently()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var repo = new AchievementRepository(db);
        var engine = new AchievementEventEngine(repo, time: new ManualTime());
        var events = new List<AchievementUnlockedEvent>();
        engine.AchievementUnlocked += (_, e) => events.Add(e);

        var published = engine.ProcessSnapshot(game, Snapshot(("A", true, 1700000000), ("B", false, null)), SnapshotKind.Baseline, "goldberg");

        Assert.Empty(published);
        Assert.Empty(events);
        var unlocks = repo.GetUnlocks(game.Id);
        Assert.Single(unlocks);
        Assert.True(unlocks["A"].IsBaseline);
    }

    [Fact]
    public void Live_transition_from_locked_to_unlocked_publishes_once()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var engine = new AchievementEventEngine(new AchievementRepository(db), time: new ManualTime());
        var events = new List<AchievementUnlockedEvent>();
        engine.AchievementUnlocked += (_, e) => events.Add(e);

        engine.ProcessSnapshot(game, Snapshot(("A", false, null)), SnapshotKind.Baseline, "goldberg");
        engine.ProcessSnapshot(game, Snapshot(("A", true, 1700000000)), SnapshotKind.Live, "goldberg");
        engine.ProcessSnapshot(game, Snapshot(("A", true, 1700000000)), SnapshotKind.Live, "goldberg");
        engine.ProcessSnapshot(game, Snapshot(("A", true, 1700000000), ("B", true, 1700000100)), SnapshotKind.Live, "goldberg");

        Assert.Equal(["A", "B"], events.Select(e => e.Unlock.ApiName));
        Assert.False(events[0].Unlock.IsBaseline);
    }

    [Fact]
    public void Duplicates_are_suppressed_across_engine_restarts()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);

        var first = new AchievementEventEngine(new AchievementRepository(db), time: new ManualTime());
        Assert.NotNull(first.Accept(game, new AchievementObservation { GameId = game.Id, ApiName = "A", Source = "test" }));

        // New process instance: in-memory cache is empty, persistence must prevent a second event.
        var second = new AchievementEventEngine(new AchievementRepository(db), time: new ManualTime());
        var count = 0;
        second.AchievementUnlocked += (_, _) => count++;
        Assert.Null(second.Accept(game, new AchievementObservation { GameId = game.Id, ApiName = "A", Source = "test" }));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Concurrent_engines_on_same_database_publish_at_most_once()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var a = new AchievementEventEngine(new AchievementRepository(db));
        var b = new AchievementEventEngine(new AchievementRepository(db));
        var obs = new AchievementObservation { GameId = game.Id, ApiName = "RACE", Source = "test" };

        var results = Enumerable.Range(0, 8).AsParallel()
            .Select(i => (i % 2 == 0 ? a : b).Accept(game, obs))
            .ToList();

        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public void Unlock_timestamp_comes_from_source_when_plausible()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var time = new ManualTime(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = new AchievementRepository(db);
        var engine = new AchievementEventEngine(repo, time: time);

        engine.ProcessSnapshot(game, Snapshot(("REAL", true, 1700000000), ("ZERO", true, null), ("FUTURE", true, 4102444800), ("ANCIENT", true, 100)),
            SnapshotKind.Live, "goldberg");

        var unlocks = repo.GetUnlocks(game.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), unlocks["REAL"].UnlockedAtUtc);
        Assert.True(unlocks["REAL"].UnlockTimeFromSource);
        Assert.Equal(time.GetUtcNow(), unlocks["ZERO"].UnlockedAtUtc);
        Assert.False(unlocks["ZERO"].UnlockTimeFromSource);
        Assert.Equal(time.GetUtcNow(), unlocks["FUTURE"].UnlockedAtUtc);
        Assert.Equal(time.GetUtcNow(), unlocks["ANCIENT"].UnlockedAtUtc);
        Assert.All(unlocks.Values, u => Assert.Equal(time.GetUtcNow(), u.ObservedAtUtc));
    }

    [Fact]
    public void Uncommitted_observations_are_ignored()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var repo = new AchievementRepository(db);
        var engine = new AchievementEventEngine(repo);

        Assert.Null(engine.Accept(game, new AchievementObservation { GameId = game.Id, ApiName = "A", Source = "api", IsCommitted = false }));
        Assert.Empty(repo.GetUnlocks(game.Id));
    }

    [Fact]
    public void Relock_in_state_file_does_not_delete_history()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var repo = new AchievementRepository(db);
        var engine = new AchievementEventEngine(repo);

        engine.ProcessSnapshot(game, Snapshot(("A", true, 1700000000)), SnapshotKind.Live, "goldberg");
        engine.ProcessSnapshot(game, Snapshot(("A", false, null)), SnapshotKind.Live, "goldberg");

        Assert.True(repo.GetUnlocks(game.Id).ContainsKey("A"));
    }

    [Fact]
    public void Throwing_subscriber_does_not_break_engine()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var engine = new AchievementEventEngine(new AchievementRepository(db));
        engine.AchievementUnlocked += (_, _) => throw new InvalidOperationException("boom");

        var evt = engine.Accept(game, new AchievementObservation { GameId = game.Id, ApiName = "A", Source = "t" });
        Assert.NotNull(evt);
        Assert.NotNull(engine.Accept(game, new AchievementObservation { GameId = game.Id, ApiName = "B", Source = "t" }));
    }

    [Fact]
    public void Events_are_enriched_with_definitions()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var game = AddGame(db);
        var lookup = new DictionaryLookup(new AchievementDefinition { ApiName = "A", DisplayName = "Alpha", Description = "d" });
        var engine = new AchievementEventEngine(new AchievementRepository(db), lookup);

        var evt = engine.Accept(game, new AchievementObservation { GameId = game.Id, ApiName = "A", Source = "t" });
        Assert.Equal("Alpha", evt!.Definition!.DisplayName);
        var unknown = engine.Accept(game, new AchievementObservation { GameId = game.Id, ApiName = "Z", Source = "t" });
        Assert.Null(unknown!.Definition);
    }

    private sealed class DictionaryLookup(params AchievementDefinition[] defs) : IAchievementDefinitionLookup
    {
        public AchievementDefinition? Find(long gameId, string apiName) => defs.FirstOrDefault(d => d.ApiName == apiName);
    }
}

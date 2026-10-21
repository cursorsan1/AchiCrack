using Achicrack.Core.Models;
using Achicrack.Core.Persistence;
using Achicrack.Core.Settings;

namespace Achicrack.Core.Tests;

public class PersistenceTests
{
    [Fact]
    public void Games_round_trip_including_executables()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var repo = new GameRepository(db);
        var game = new GameRecord
        {
            Name = "Test Game",
            InstallDirectory = @"D:\Games\Test",
            AppId = 480,
            AppIdSource = "steam_appid.txt",
            AdapterId = "goldberg",
            SteamApiDirectory = @"D:\Games\Test\bin",
            Executables = [@"D:\Games\Test\bin\Test.exe"],
            AddedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };

        var id = repo.Add(game);
        var loaded = repo.Get(id)!;
        Assert.Equal("Test Game", loaded.Name);
        Assert.Equal(480u, loaded.AppId);
        Assert.Equal(game.Executables, loaded.Executables);
        Assert.Equal(game.AddedAtUtc, loaded.AddedAtUtc);
        Assert.Null(loaded.LastPlayedAtUtc);

        var played = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        repo.SetLastPlayed(id, played);
        Assert.Equal(played, repo.Get(id)!.LastPlayedAtUtc);

        loaded.Name = "Renamed";
        repo.Update(loaded);
        Assert.Equal("Renamed", repo.GetAll().Single().Name);
    }

    [Fact]
    public void Unlocks_persist_across_connections_and_cascade_on_delete()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var games = new GameRepository(db);
        var id = games.Add(new GameRecord { Name = "G", InstallDirectory = "/g" });
        var achievements = new AchievementRepository(db);
        var unlock = new UnlockRecord
        {
            GameId = id, ApiName = "A", UnlockedAtUtc = DateTimeOffset.FromUnixTimeSeconds(1700000000),
            ObservedAtUtc = DateTimeOffset.FromUnixTimeSeconds(1700000005), Source = "goldberg", UnlockTimeFromSource = true,
        };

        Assert.True(achievements.TryInsertUnlock(unlock));
        Assert.False(achievements.TryInsertUnlock(unlock with { Source = "other" }));

        // Simulates application restart: a brand-new database object over the same file.
        var reopened = new AchicrackDatabase(db.DatabasePath);
        reopened.Initialize();
        var stored = new AchievementRepository(reopened).GetUnlocks(id)["A"];
        Assert.Equal(unlock, stored);

        games.Delete(id);
        Assert.Empty(achievements.GetUnlocks(id));
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        db.Initialize();
        db.Initialize();
        Assert.Equal(1, db.SchemaVersion);
    }

    [Fact]
    public void Catalog_round_trip_and_stats()
    {
        using var dir = new TempDir();
        var db = TestDb.Create(dir);
        var id = new GameRepository(db).Add(new GameRecord { Name = "G", InstallDirectory = "/g" });
        var repo = new AchievementRepository(db);
        var catalog = new AchievementCatalog
        {
            Definitions =
            [
                new AchievementDefinition { ApiName = "A", DisplayName = "Alpha", SortOrder = 0, GlobalPercent = 12.5 },
                new AchievementDefinition { ApiName = "B", Hidden = true, SortOrder = 1 },
            ],
            IsComplete = true,
            Source = "manifest",
            RetrievedAtUtc = DateTimeOffset.FromUnixTimeSeconds(1700000000),
        };

        repo.SaveCatalog(id, catalog);
        repo.SaveCatalog(id, catalog); // replace, not duplicate
        var loaded = repo.LoadCatalog(id)!;
        Assert.Equal(catalog.Definitions, loaded.Definitions);
        Assert.True(loaded.IsComplete);

        repo.TryInsertUnlock(new UnlockRecord
        {
            GameId = id, ApiName = "A", UnlockedAtUtc = DateTimeOffset.UtcNow, ObservedAtUtc = DateTimeOffset.UtcNow, Source = "t",
        });
        var stats = repo.GetStats().Single();
        Assert.Equal((1, 2, true), (stats.Unlocked, stats.CatalogTotal, stats.CatalogComplete));

        var recent = repo.GetRecentUnlocks(10).Single();
        Assert.Equal("Alpha", recent.DisplayName);
        Assert.Equal("G", recent.GameName);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips_and_normalizes()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("settings.json"));
        store.Save(new AppSettings
        {
            StartWithWindows = true,
            Notification = new NotificationStyle { Position = NotificationPosition.BottomLeft, Scale = 99, DurationSeconds = 0.1 },
            Presets = [new NotificationPreset("Big", new NotificationStyle { Scale = 1.5 })],
        });

        var reloaded = new SettingsStore(dir.Combine("settings.json")).Current;
        Assert.True(reloaded.StartWithWindows);
        Assert.Equal(NotificationPosition.BottomLeft, reloaded.Notification.Position);
        Assert.Equal(2.5, reloaded.Notification.Scale);
        Assert.Equal(1.5, reloaded.Notification.DurationSeconds);
        Assert.Equal("Big", reloaded.Presets.Single().Name);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_with_backup()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ this is not json");
        var settings = new SettingsStore(path).Current;
        Assert.Equal(new AppSettings().Notification, settings.Notification);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.corrupt-*"));
    }
}

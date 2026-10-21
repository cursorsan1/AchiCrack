using Achicrack.Core.Adapters;
using Achicrack.Core.Adapters.Goldberg;
using Achicrack.Core.Catalog;
using Achicrack.Core.Library;
using Achicrack.Core.Models;
using Achicrack.Core.Persistence;
using Achicrack.FakeGame;

namespace Achicrack.Core.Tests;

public class CatalogTests
{
    private static (AchievementCatalogService Service, GameRecord Game, GoldbergFixture Fixture, IconCache Icons, AchievementRepository Repo)
        Setup(TempDir dir, bool withIcons = true, int count = 5)
    {
        var fx = new GoldbergFixture(dir.Combine("game"), 4000002, dir.Combine("appdata", "GSE Saves")).Create(count, withIcons: withIcons);
        var db = TestDb.Create(dir);
        var games = new GameRepository(db);
        var game = new GameRecord
        {
            Name = "Fake", InstallDirectory = fx.GameDirectory, AppId = fx.AppId, AdapterId = GoldbergAdapter.AdapterId,
            SteamApiDirectory = fx.BinDirectory,
        };
        games.Add(game);
        var repo = new AchievementRepository(db);
        var icons = new IconCache(dir.Combine("cache"));
        var registry = new AdapterRegistry([new GoldbergAdapter(new TestEnvironment(dir.Combine("appdata")))]);
        return (new AchievementCatalogService(registry, repo, icons), game, fx, icons, repo);
    }

    [Fact]
    public void Loads_complete_catalog_from_manifest_and_caches_icons()
    {
        using var dir = new TempDir();
        var (service, game, fx, icons, _) = Setup(dir);

        var catalog = service.GetCatalog(game);

        Assert.True(catalog.IsComplete);
        Assert.Equal(5, catalog.Definitions.Count);
        Assert.Equal("Fake Achievement 2", catalog.Find("ACH_FAKE_02")!.DisplayName); // localized object
        Assert.True(catalog.Find("ACH_FAKE_05")!.Hidden);
        Assert.All(catalog.Definitions, d =>
        {
            Assert.NotNull(d.IconPath);
            Assert.StartsWith(icons.CacheDirectory, d.IconPath);
            Assert.True(File.Exists(d.IconPath));
        });
        Assert.Equal("Fake Achievement 1", service.Find(game.Id, "ACH_FAKE_01")!.DisplayName);
    }

    [Fact]
    public void Missing_icons_resolve_to_null()
    {
        using var dir = new TempDir();
        var (service, game, _, _, _) = Setup(dir, withIcons: false);
        Assert.All(service.GetCatalog(game).Definitions, d => Assert.Null(d.IconPath));
    }

    [Fact]
    public void Falls_back_to_persisted_catalog_when_manifest_disappears()
    {
        using var dir = new TempDir();
        var (service, game, fx, _, _) = Setup(dir);
        service.GetCatalog(game);
        File.Delete(Path.Combine(fx.SteamSettingsDirectory, "achievements.json"));
        Directory.Delete(Path.Combine(fx.SteamSettingsDirectory, "images"), recursive: true);

        var catalog = service.GetCatalog(game, refresh: true);

        Assert.Equal(5, catalog.Definitions.Count);
        Assert.All(catalog.Definitions, d => Assert.True(File.Exists(d.IconPath))); // cached copies survive
    }

    [Fact]
    public void No_manifest_produces_incomplete_empty_catalog()
    {
        using var dir = new TempDir();
        var (service, game, fx, _, _) = Setup(dir);
        File.Delete(Path.Combine(fx.SteamSettingsDirectory, "achievements.json"));

        var catalog = service.GetCatalog(game, refresh: true);

        Assert.False(catalog.IsComplete);
        Assert.Empty(catalog.Definitions);
    }

    [Fact]
    public void Corrupt_manifest_is_handled()
    {
        using var dir = new TempDir();
        var (service, game, fx, _, _) = Setup(dir);
        File.WriteAllText(Path.Combine(fx.SteamSettingsDirectory, "achievements.json"), "[{ broken");
        var catalog = service.GetCatalog(game, refresh: true);
        Assert.False(catalog.IsComplete);
    }

    [Fact]
    public async Task Online_rarity_enrichment_updates_catalog()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("game"), 4000003, dir.Combine("appdata", "GSE Saves")).Create(2);
        var db = TestDb.Create(dir);
        var game = new GameRecord { Name = "F", InstallDirectory = fx.GameDirectory, AppId = fx.AppId, AdapterId = "goldberg", SteamApiDirectory = fx.BinDirectory };
        new GameRepository(db).Add(game);
        var service = new AchievementCatalogService(
            new AdapterRegistry([new GoldbergAdapter(new TestEnvironment(dir.Combine("appdata")))]),
            new AchievementRepository(db), new IconCache(dir.Combine("cache")),
            online: new FakeOnline(new Dictionary<string, double> { ["ACH_FAKE_01"] = 3.2 }));

        Assert.True(await service.TryEnrichRarityAsync(game, CancellationToken.None));

        var def = service.GetCatalog(game).Find("ACH_FAKE_01")!;
        Assert.Equal(3.2, def.GlobalPercent);
        Assert.Equal(RarityTier.UltraRare, Rarity.FromPercent(def.GlobalPercent));
    }

    [Fact]
    public void Steam_rarity_response_parsing()
    {
        var parsed = SteamGlobalRarityProvider.Parse("""
            {"achievementpercentages":{"achievements":[{"name":"A","percent":"12.5"},{"name":"B","percent":3},{"name":"C","percent":"x"}]}}
            """)!;
        Assert.Equal(12.5, parsed["A"]);
        Assert.Equal(3, parsed["B"]);
        Assert.False(parsed.ContainsKey("C"));
    }

    private sealed class FakeOnline(IReadOnlyDictionary<string, double> data) : IOnlineMetadataProvider
    {
        public Task<IReadOnlyDictionary<string, double>?> GetGlobalPercentagesAsync(uint appId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, double>?>(data);
    }
}

public class AchievementListBuilderTests
{
    private static UnlockRecord Unlock(string api, long t) => new()
    {
        GameId = 1, ApiName = api, UnlockedAtUtc = DateTimeOffset.FromUnixTimeSeconds(t),
        ObservedAtUtc = DateTimeOffset.FromUnixTimeSeconds(t), Source = "t",
    };

    [Fact]
    public void Merges_locked_and_unlocked_with_completion()
    {
        var catalog = new AchievementCatalog
        {
            Definitions =
            [
                new AchievementDefinition { ApiName = "A", DisplayName = "Alpha", SortOrder = 0 },
                new AchievementDefinition { ApiName = "B", DisplayName = "Beta", SortOrder = 1 },
                new AchievementDefinition { ApiName = "C", SortOrder = 2 },
            ],
            IsComplete = true,
            Source = "manifest",
        };
        var unlocks = new Dictionary<string, UnlockRecord> { ["A"] = Unlock("A", 1700000000), ["C"] = Unlock("C", 1700000500) };

        var summary = AchievementListBuilder.Build(catalog, unlocks);

        Assert.Equal(["A", "B", "C"], summary.Items.Select(i => i.ApiName));
        Assert.Equal([true, false, true], summary.Items.Select(i => i.IsUnlocked));
        Assert.Null(summary.Items[1].UnlockedAtUtc);
        Assert.Equal("C", summary.Items[2].Title); // unknown name → API identifier
        Assert.Equal(2, summary.Unlocked);
        Assert.Equal(3, summary.Total);
        Assert.Equal(66.7, summary.CompletionPercent);
        Assert.Equal("C", summary.LastUnlocked!.ApiName);
    }

    [Fact]
    public void Incomplete_catalog_has_no_total_and_lists_unknown_unlocks()
    {
        var catalog = AchievementCatalog.Empty("none");
        var summary = AchievementListBuilder.Build(catalog,
            new Dictionary<string, UnlockRecord> { ["X"] = Unlock("X", 1700000000) });

        Assert.False(summary.IsCatalogComplete);
        Assert.Null(summary.Total);
        Assert.Null(summary.CompletionPercent);
        var item = Assert.Single(summary.Items);
        Assert.False(item.InCatalog);
        Assert.Equal("X", item.Title);
    }

    [Theory]
    [InlineData(null, RarityTier.Unknown)]
    [InlineData(75.0, RarityTier.Common)]
    [InlineData(35.0, RarityTier.Rare)]
    [InlineData(10.0, RarityTier.VeryRare)]
    [InlineData(1.0, RarityTier.UltraRare)]
    public void Rarity_tiers(double? percent, RarityTier expected) => Assert.Equal(expected, Rarity.FromPercent(percent));
}

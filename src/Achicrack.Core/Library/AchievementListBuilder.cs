using Achicrack.Core.Models;

namespace Achicrack.Core.Library;

/// <summary>Rarity tiers (PS5-style labels) derived from the global unlock percentage.</summary>
public enum RarityTier
{
    Unknown,
    Common,
    Rare,
    VeryRare,
    UltraRare,
}

public static class Rarity
{
    public static RarityTier FromPercent(double? percent) => percent switch
    {
        null => RarityTier.Unknown,
        > 50 => RarityTier.Common,
        > 20 => RarityTier.Rare,
        > 5 => RarityTier.VeryRare,
        _ => RarityTier.UltraRare,
    };

    public static string Label(RarityTier tier) => tier switch
    {
        RarityTier.Common => "Common",
        RarityTier.Rare => "Rare",
        RarityTier.VeryRare => "Very Rare",
        RarityTier.UltraRare => "Ultra Rare",
        _ => "",
    };
}

public sealed record AchievementListItem
{
    public required string ApiName { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Hidden { get; init; }
    public string? IconPath { get; init; }
    public string? IconGrayPath { get; init; }
    public bool IsUnlocked { get; init; }
    public DateTimeOffset? UnlockedAtUtc { get; init; }
    public double? GlobalPercent { get; init; }

    /// <summary>False for unlocks seen in the state file that the catalog does not define.</summary>
    public bool InCatalog { get; init; }

    public RarityTier Rarity => Library.Rarity.FromPercent(GlobalPercent);

    /// <summary>Display title: metadata name or, if unknown, the API identifier (never invented).</summary>
    public string Title => DisplayName ?? ApiName;
}

public sealed record GameAchievementSummary
{
    public required IReadOnlyList<AchievementListItem> Items { get; init; }
    public required int Unlocked { get; init; }

    /// <summary>Total achievement count, only when the catalog is complete.</summary>
    public int? Total { get; init; }

    public required bool IsCatalogComplete { get; init; }
    public double? CompletionPercent { get; init; }
    public AchievementListItem? LastUnlocked { get; init; }
    public required string CatalogSource { get; init; }
    public string? CatalogNotes { get; init; }
}

/// <summary>Merges the catalog (definitions) with persisted unlocks into the list shown in the UI.</summary>
public static class AchievementListBuilder
{
    public static GameAchievementSummary Build(AchievementCatalog catalog, IReadOnlyDictionary<string, UnlockRecord> unlocks)
    {
        var items = new List<AchievementListItem>(catalog.Definitions.Count + unlocks.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in catalog.Definitions.OrderBy(d => d.SortOrder))
        {
            seen.Add(d.ApiName);
            unlocks.TryGetValue(d.ApiName, out var unlock);
            items.Add(new AchievementListItem
            {
                ApiName = d.ApiName,
                DisplayName = d.DisplayName,
                Description = d.Description,
                Hidden = d.Hidden,
                IconPath = d.IconPath,
                IconGrayPath = d.IconGrayPath,
                GlobalPercent = d.GlobalPercent,
                IsUnlocked = unlock is not null,
                UnlockedAtUtc = unlock?.UnlockedAtUtc,
                InCatalog = true,
            });
        }

        // Unlocks the catalog does not know about: list them by API name only.
        foreach (var unlock in unlocks.Values.Where(u => !seen.Contains(u.ApiName)).OrderBy(u => u.UnlockedAtUtc))
        {
            items.Add(new AchievementListItem
            {
                ApiName = unlock.ApiName,
                IsUnlocked = true,
                UnlockedAtUtc = unlock.UnlockedAtUtc,
                InCatalog = false,
            });
        }

        var unlocked = items.Count(i => i.IsUnlocked);
        int? total = catalog.IsComplete ? items.Count : null;
        return new GameAchievementSummary
        {
            Items = items,
            Unlocked = unlocked,
            Total = total,
            IsCatalogComplete = catalog.IsComplete,
            CompletionPercent = total is > 0 ? Math.Round(100.0 * unlocked / total.Value, 1) : null,
            LastUnlocked = items.Where(i => i.IsUnlocked).MaxBy(i => i.UnlockedAtUtc),
            CatalogSource = catalog.Source,
            CatalogNotes = catalog.Notes,
        };
    }
}

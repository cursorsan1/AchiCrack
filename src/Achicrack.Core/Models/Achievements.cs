namespace Achicrack.Core.Models;

/// <summary>Definition of one achievement as declared by a metadata source.</summary>
public sealed record AchievementDefinition
{
    /// <summary>Steamworks API name (e.g. ACH_WIN_ONE_GAME). Always present.</summary>
    public required string ApiName { get; init; }

    /// <summary>Display name from metadata. Null when unknown — never fabricated.</summary>
    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public bool Hidden { get; init; }

    /// <summary>Absolute path of the unlocked icon (source or cached copy).</summary>
    public string? IconPath { get; init; }

    /// <summary>Absolute path of the locked (gray) icon.</summary>
    public string? IconGrayPath { get; init; }

    /// <summary>Global unlock percentage (0-100) when available.</summary>
    public double? GlobalPercent { get; init; }

    public int SortOrder { get; init; }
}

/// <summary>The set of achievement definitions known for one game.</summary>
public sealed record AchievementCatalog
{
    public static AchievementCatalog Empty(string reason) => new()
    {
        Definitions = [],
        IsComplete = false,
        Source = "none",
        Notes = reason,
    };

    public required IReadOnlyList<AchievementDefinition> Definitions { get; init; }

    /// <summary>
    /// True only when the source declares the full achievement list (e.g. an emulator
    /// manifest generated from the Steam schema). State files alone never produce a complete catalog.
    /// </summary>
    public required bool IsComplete { get; init; }

    public required string Source { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset RetrievedAtUtc { get; init; }

    public AchievementDefinition? Find(string apiName) =>
        Definitions.FirstOrDefault(d => string.Equals(d.ApiName, apiName, StringComparison.Ordinal));
}

/// <summary>One entry of a raw achievement state file.</summary>
public readonly record struct AchievementStateEntry(string ApiName, bool Earned, DateTimeOffset? EarnedTimeUtc);

/// <summary>Parsed contents of one or more achievement state sources at a point in time.</summary>
public sealed class AchievementStateSnapshot
{
    public static AchievementStateSnapshot Empty { get; } = new(new Dictionary<string, AchievementStateEntry>(), []);

    public AchievementStateSnapshot(IReadOnlyDictionary<string, AchievementStateEntry> entries, IReadOnlyList<string> warnings)
    {
        Entries = entries;
        Warnings = warnings;
    }

    public IReadOnlyDictionary<string, AchievementStateEntry> Entries { get; }

    /// <summary>Non-fatal problems found while parsing (skipped entries, odd values).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public IEnumerable<AchievementStateEntry> Earned => Entries.Values.Where(e => e.Earned);

    /// <summary>Union of several snapshots: an achievement is earned if any source says so (earliest time wins).</summary>
    public static AchievementStateSnapshot Merge(IEnumerable<AchievementStateSnapshot> snapshots)
    {
        var merged = new Dictionary<string, AchievementStateEntry>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var snapshot in snapshots)
        {
            warnings.AddRange(snapshot.Warnings);
            foreach (var entry in snapshot.Entries.Values)
            {
                if (!merged.TryGetValue(entry.ApiName, out var existing))
                {
                    merged[entry.ApiName] = entry;
                    continue;
                }

                if (entry.Earned && !existing.Earned)
                {
                    merged[entry.ApiName] = entry;
                }
                else if (entry.Earned && existing.Earned && entry.EarnedTimeUtc is { } t &&
                         (existing.EarnedTimeUtc is null || t < existing.EarnedTimeUtc))
                {
                    merged[entry.ApiName] = entry;
                }
            }
        }

        return new AchievementStateSnapshot(merged, warnings);
    }
}

/// <summary>Persisted fact that an achievement was observed as unlocked.</summary>
public sealed record UnlockRecord
{
    public required long GameId { get; init; }
    public required string ApiName { get; init; }

    /// <summary>Unlock time reported by the source, or the observation time if the source had none.</summary>
    public required DateTimeOffset UnlockedAtUtc { get; init; }

    /// <summary>True when <see cref="UnlockedAtUtc"/> came from the source rather than our clock.</summary>
    public bool UnlockTimeFromSource { get; init; }

    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Adapter/source identifier (e.g. "goldberg", "simulated").</summary>
    public required string Source { get; init; }

    /// <summary>True when the unlock pre-dated monitoring (baseline import, no notification).</summary>
    public bool IsBaseline { get; init; }
}

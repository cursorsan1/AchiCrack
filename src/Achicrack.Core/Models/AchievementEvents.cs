namespace Achicrack.Core.Models;

/// <summary>
/// Normalized observation produced by any adapter (state file, simulated source, future API integrations).
/// </summary>
public sealed record AchievementObservation
{
    public required long GameId { get; init; }
    public required string ApiName { get; init; }
    public required string Source { get; init; }

    /// <summary>Unlock time reported by the source, if any.</summary>
    public DateTimeOffset? ReportedUnlockTimeUtc { get; init; }

    /// <summary>
    /// True when the unlock is known to be committed (persisted by the Steam API implementation).
    /// An uncommitted observation (e.g. a SetAchievement call without StoreStats) is never notified.
    /// </summary>
    public bool IsCommitted { get; init; } = true;
}

/// <summary>How the event engine should treat an incoming snapshot.</summary>
public enum SnapshotKind
{
    /// <summary>First read when monitoring starts: unlocks are recorded silently.</summary>
    Baseline,

    /// <summary>A change observed while monitoring: new unlocks are announced.</summary>
    Live,
}

/// <summary>Published once per genuinely new unlock.</summary>
public sealed record AchievementUnlockedEvent(GameRecord Game, UnlockRecord Unlock, AchievementDefinition? Definition);

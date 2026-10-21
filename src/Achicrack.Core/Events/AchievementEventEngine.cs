using Achicrack.Core.Models;
using Achicrack.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Events;

/// <summary>Looks up achievement metadata for event enrichment.</summary>
public interface IAchievementDefinitionLookup
{
    AchievementDefinition? Find(long gameId, string apiName);
}

/// <summary>
/// Central point every adapter feeds. Validates observations, deduplicates against persisted unlocks,
/// persists new unlocks and publishes <see cref="AchievementUnlocked"/> for live (non-baseline) unlocks only.
/// </summary>
public sealed class AchievementEventEngine
{
    /// <summary>Steam launched in 2003; earlier timestamps from a state file are treated as bogus.</summary>
    private static readonly DateTimeOffset MinPlausibleUnlockTime = new(2003, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly AchievementRepository _repository;
    private readonly IAchievementDefinitionLookup? _definitions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, HashSet<string>> _known = [];

    public AchievementEventEngine(
        AchievementRepository repository,
        IAchievementDefinitionLookup? definitions = null,
        TimeProvider? time = null,
        ILogger<AchievementEventEngine>? logger = null)
    {
        _repository = repository;
        _definitions = definitions;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Raised (on the caller's thread) for every new live unlock, after it has been persisted.</summary>
    public event EventHandler<AchievementUnlockedEvent>? AchievementUnlocked;

    /// <summary>
    /// Processes a full state snapshot. Every earned entry that is not yet persisted is stored; only
    /// <see cref="SnapshotKind.Live"/> snapshots produce events.
    /// </summary>
    public IReadOnlyList<AchievementUnlockedEvent> ProcessSnapshot(
        GameRecord game, AchievementStateSnapshot snapshot, SnapshotKind kind, string source)
    {
        var published = new List<AchievementUnlockedEvent>();
        foreach (var entry in snapshot.Earned.OrderBy(e => e.EarnedTimeUtc ?? DateTimeOffset.MaxValue))
        {
            var evt = Accept(game, new AchievementObservation
            {
                GameId = game.Id,
                ApiName = entry.ApiName,
                Source = source,
                ReportedUnlockTimeUtc = entry.EarnedTimeUtc,
                IsCommitted = true,
            }, kind);
            if (evt is not null)
            {
                published.Add(evt);
            }
        }

        return published;
    }

    /// <summary>Accepts a single normalized observation. Returns the published event, or null if suppressed.</summary>
    public AchievementUnlockedEvent? Accept(GameRecord game, AchievementObservation observation, SnapshotKind kind = SnapshotKind.Live)
    {
        if (observation.GameId != game.Id)
        {
            throw new ArgumentException("Observation belongs to a different game.", nameof(observation));
        }

        if (string.IsNullOrWhiteSpace(observation.ApiName))
        {
            _logger.LogWarning("Ignoring observation with empty API name for {Game}", game.Name);
            return null;
        }

        if (!observation.IsCommitted)
        {
            // An uncommitted unlock (e.g. SetAchievement without a successful StoreStats) may never persist.
            _logger.LogDebug("Ignoring uncommitted observation {Api} for {Game}", observation.ApiName, game.Name);
            return null;
        }

        var now = _time.GetUtcNow();
        UnlockRecord record;
        lock (_gate)
        {
            var known = GetKnown(game.Id);
            if (known.Contains(observation.ApiName))
            {
                return null;
            }

            var (unlockTime, fromSource) = ResolveUnlockTime(observation.ReportedUnlockTimeUtc, now);
            record = new UnlockRecord
            {
                GameId = game.Id,
                ApiName = observation.ApiName,
                UnlockedAtUtc = unlockTime,
                UnlockTimeFromSource = fromSource,
                ObservedAtUtc = now,
                Source = observation.Source,
                IsBaseline = kind == SnapshotKind.Baseline,
            };

            var inserted = _repository.TryInsertUnlock(record);
            known.Add(observation.ApiName);
            if (!inserted)
            {
                // Another writer (or a previous process instance) already stored it.
                return null;
            }
        }

        if (kind == SnapshotKind.Baseline)
        {
            _logger.LogInformation("Baseline unlock recorded silently: {Game} / {Api}", game.Name, record.ApiName);
            return null;
        }

        _logger.LogInformation("New unlock: {Game} / {Api} at {Time:o} (source {Source})",
            game.Name, record.ApiName, record.UnlockedAtUtc, record.Source);
        var evt = new AchievementUnlockedEvent(game, record, _definitions?.Find(game.Id, record.ApiName));
        try
        {
            AchievementUnlocked?.Invoke(this, evt);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not break monitoring; the unlock is already persisted.
            _logger.LogError(ex, "AchievementUnlocked subscriber failed for {Game} / {Api}", game.Name, record.ApiName);
        }

        return evt;
    }

    /// <summary>Drops the in-memory cache for a game (e.g. after it was deleted).</summary>
    public void Forget(long gameId)
    {
        lock (_gate)
        {
            _known.Remove(gameId);
        }
    }

    internal static (DateTimeOffset Time, bool FromSource) ResolveUnlockTime(DateTimeOffset? reported, DateTimeOffset now)
    {
        if (reported is { } t && t >= MinPlausibleUnlockTime && t <= now.AddDays(1))
        {
            return (t, true);
        }

        return (now, false);
    }

    private HashSet<string> GetKnown(long gameId)
    {
        if (!_known.TryGetValue(gameId, out var set))
        {
            set = new HashSet<string>(_repository.GetUnlocks(gameId).Keys, StringComparer.Ordinal);
            _known[gameId] = set;
        }

        return set;
    }
}

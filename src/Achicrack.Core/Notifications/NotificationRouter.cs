using Achicrack.Core.Events;
using Achicrack.Core.Library;
using Achicrack.Core.Models;
using Achicrack.Core.Persistence;
using Achicrack.Core.Settings;
using Achicrack.Core.Tracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Notifications;

/// <summary>
/// Turns domain events (new unlock, tracking started) into notification requests.
/// Keeps notification rendering fully separate from detection.
/// </summary>
public sealed class NotificationRouter : IDisposable
{
    private readonly AchievementEventEngine _engine;
    private readonly TrackingCoordinator _coordinator;
    private readonly NotificationQueue _queue;
    private readonly AchievementRepository _achievements;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public NotificationRouter(AchievementEventEngine engine, TrackingCoordinator coordinator, NotificationQueue queue,
        AchievementRepository achievements, Func<AppSettings> settings, TimeProvider? time = null,
        ILogger<NotificationRouter>? logger = null)
    {
        _engine = engine;
        _coordinator = coordinator;
        _queue = queue;
        _achievements = achievements;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _engine.AchievementUnlocked += OnUnlocked;
        _coordinator.TrackingStarted += OnTrackingStarted;
    }

    public static NotificationRequest ForUnlock(AchievementUnlockedEvent e)
    {
        var def = e.Definition;
        return new NotificationRequest
        {
            Kind = NotificationKind.AchievementUnlocked,
            Header = "Achievement Unlocked",
            Title = def?.DisplayName ?? e.Unlock.ApiName,
            Message = def?.Description,
            IconPath = def?.IconPath,
            GlobalPercent = def?.GlobalPercent,
            Rarity = Rarity.FromPercent(def?.GlobalPercent),
            DedupKey = $"unlock:{e.Game.Id}:{e.Unlock.ApiName}",
            GameId = e.Game.Id,
            ApiName = e.Unlock.ApiName,
        };
    }

    public static NotificationRequest ForTrackingStarted(GameRecord game, AchievementCatalog? catalog, int unlocked)
    {
        var progress = catalog is { IsComplete: true, Definitions.Count: > 0 }
            ? $" {unlocked} / {catalog.Definitions.Count} achievements."
            : "";
        return new NotificationRequest
        {
            Kind = NotificationKind.TrackingStarted,
            Header = "Started Tracking",
            Title = game.Name,
            Message = "Achievement tracking is now active." + progress,
            IconPath = game.IconPath,
            DedupKey = $"tracking:{game.Id}",
            GameId = game.Id,
        };
    }

    public void Dispose()
    {
        _engine.AchievementUnlocked -= OnUnlocked;
        _coordinator.TrackingStarted -= OnTrackingStarted;
    }

    private void OnUnlocked(object? sender, AchievementUnlockedEvent e)
    {
        if (!_settings().ShowAchievementNotification)
        {
            return;
        }

        if (_queue.Enqueue(ForUnlock(e)))
        {
            try
            {
                _achievements.MarkNotified(e.Game.Id, e.Unlock.ApiName, _time.GetUtcNow());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not mark {Api} notified", e.Unlock.ApiName);
            }
        }
    }

    private void OnTrackingStarted(object? sender, TrackingSessionInfo info)
    {
        if (!_settings().ShowTrackingStartedNotification)
        {
            return;
        }

        var unlocked = 0;
        try
        {
            unlocked = _achievements.GetUnlocks(info.Game.Id).Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count unlocks for {Game}", info.Game.Name);
        }

        _queue.Enqueue(ForTrackingStarted(info.Game, info.Catalog, unlocked));
    }
}

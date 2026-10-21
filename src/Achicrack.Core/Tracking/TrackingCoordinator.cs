using System.Collections.Concurrent;
using Achicrack.Core.Adapters;
using Achicrack.Core.Catalog;
using Achicrack.Core.Events;
using Achicrack.Core.Models;
using Achicrack.Core.Monitoring;
using Achicrack.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Tracking;

public enum TrackingState
{
    GameDetected,
    InitializingTracker,
    TrackingActive,
    TrackingUnavailable,
    TrackingStopped,
}

/// <summary>Immutable view of a tracking session, published on every state change.</summary>
public sealed record TrackingSessionInfo
{
    public required GameRecord Game { get; init; }
    public required TrackingState State { get; init; }
    public string? StatusMessage { get; init; }
    public IReadOnlyList<string> WatchedPaths { get; init; } = [];
    public DateTimeOffset ChangedAtUtc { get; init; }
    public AchievementCatalog? Catalog { get; init; }
}

/// <summary>
/// Owns the per-game monitoring lifecycle: GameDetected → InitializingTracker → TrackingActive
/// (or TrackingUnavailable) → TrackingStopped. "Tracking started" is only raised after the adapter's
/// monitor has successfully read its baseline.
/// </summary>
public sealed class TrackingCoordinator : IAsyncDisposable
{
    private readonly AdapterRegistry _adapters;
    private readonly AchievementEventEngine _engine;
    private readonly AchievementCatalogService _catalogs;
    private readonly GameRepository _games;
    private readonly Func<FileStateMonitorOptions> _monitorOptions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, Session> _sessions = new();

    public TrackingCoordinator(
        AdapterRegistry adapters,
        AchievementEventEngine engine,
        AchievementCatalogService catalogs,
        GameRepository games,
        Func<FileStateMonitorOptions>? monitorOptions = null,
        TimeProvider? time = null,
        ILogger<TrackingCoordinator>? logger = null)
    {
        _adapters = adapters;
        _engine = engine;
        _catalogs = catalogs;
        _games = games;
        _monitorOptions = monitorOptions ?? (() => new FileStateMonitorOptions());
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Raised on every state transition.</summary>
    public event EventHandler<TrackingSessionInfo>? SessionChanged;

    /// <summary>Raised once per session, when the monitor is active.</summary>
    public event EventHandler<TrackingSessionInfo>? TrackingStarted;

    public IReadOnlyList<TrackingSessionInfo> Sessions => _sessions.Values.Select(s => s.Info).ToArray();

    public TrackingSessionInfo? GetSession(long gameId) => _sessions.TryGetValue(gameId, out var s) ? s.Info : null;

    public IAchievementStateMonitor? GetMonitor(long gameId) => _sessions.TryGetValue(gameId, out var s) ? s.Monitor : null;

    public async Task<TrackingSessionInfo> StartTrackingAsync(GameRecord game, CancellationToken cancellationToken = default)
    {
        var session = _sessions.GetOrAdd(game.Id, _ => new Session(game));
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Info.State is TrackingState.TrackingActive or TrackingState.InitializingTracker && session.Monitor is not null)
            {
                return session.Info; // already tracking (e.g. a second process of the same game)
            }

            session.Game = game;
            Transition(session, TrackingState.GameDetected, "Game detected.");
            Transition(session, TrackingState.InitializingTracker, "Initializing achievement tracker…");

            var adapter = _adapters.Get(game.AdapterId);
            if (adapter is null)
            {
                return Transition(session, TrackingState.TrackingUnavailable,
                    "No supported achievement source was found for this game.");
            }

            AchievementCatalog catalog;
            try
            {
                catalog = _catalogs.GetCatalog(game, refresh: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog load failed for {Game}", game.Name);
                catalog = AchievementCatalog.Empty("Catalog could not be loaded.");
            }

            session.Catalog = catalog;

            IAchievementStateMonitor monitor;
            try
            {
                monitor = adapter.CreateMonitor(game, _monitorOptions(), _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create monitor for {Game}", game.Name);
                return Transition(session, TrackingState.TrackingUnavailable, ex.Message);
            }

            try
            {
                await monitor.StartAsync((snapshot, kind) => OnSnapshot(session, monitor, snapshot, kind), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Monitor start failed for {Game}", game.Name);
                await monitor.DisposeAsync().ConfigureAwait(false);
                return Transition(session, TrackingState.TrackingUnavailable, $"Monitor failed to start: {ex.Message}");
            }

            session.Monitor = monitor;
            var info = Transition(session, TrackingState.TrackingActive, "Achievement tracking is now active.");
            Raise(TrackingStarted, info);
            return info;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task StopTrackingAsync(long gameId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(gameId, out var session))
        {
            return;
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Monitor is { } monitor)
            {
                try
                {
                    // Catch unlocks written just before the game exited.
                    await monitor.ReconcileNowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Final reconcile failed for {Game}", session.Game.Name);
                }

                await monitor.StopAsync().ConfigureAwait(false);
                await monitor.DisposeAsync().ConfigureAwait(false);
                session.Monitor = null;
            }

            try
            {
                _games.SetLastPlayed(gameId, _time.GetUtcNow());
                session.Game.LastPlayedAtUtc = _time.GetUtcNow();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update last-played for {Game}", session.Game.Name);
            }

            Transition(session, TrackingState.TrackingStopped, "Game closed. Tracking stopped.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToArray())
        {
            await StopTrackingAsync(id).ConfigureAwait(false);
        }
    }

    private void OnSnapshot(Session session, IAchievementStateMonitor monitor, AchievementStateSnapshot snapshot, SnapshotKind kind)
    {
        foreach (var warning in snapshot.Warnings)
        {
            _logger.LogDebug("State warning for {Game}: {Warning}", session.Game.Name, warning);
        }

        _engine.ProcessSnapshot(session.Game, snapshot, kind, monitor.Source);
    }

    private TrackingSessionInfo Transition(Session session, TrackingState state, string message)
    {
        session.Info = new TrackingSessionInfo
        {
            Game = session.Game,
            State = state,
            StatusMessage = message,
            WatchedPaths = session.Monitor?.WatchedPaths ?? _adapters.Get(session.Game.AdapterId)?.GetStatePaths(session.Game) ?? [],
            ChangedAtUtc = _time.GetUtcNow(),
            Catalog = session.Catalog,
        };
        _logger.LogInformation("Tracking {Game}: {State} — {Message}", session.Game.Name, state, message);
        Raise(SessionChanged, session.Info);
        return session.Info;
    }

    private void Raise(EventHandler<TrackingSessionInfo>? handler, TrackingSessionInfo info)
    {
        try
        {
            handler?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tracking event handler failed");
        }
    }

    private sealed class Session(GameRecord game)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public GameRecord Game { get; set; } = game;
        public IAchievementStateMonitor? Monitor { get; set; }
        public AchievementCatalog? Catalog { get; set; }

        public TrackingSessionInfo Info { get; set; } = new() { Game = game, State = TrackingState.TrackingStopped };
    }
}

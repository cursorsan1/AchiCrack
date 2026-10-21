using Achicrack.Core.Detection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Tracking;

/// <summary>Glue between process detection and the tracking coordinator (automatic start/stop).</summary>
public sealed class AutoTrackingService : IDisposable
{
    private readonly GameDetectionService _detection;
    private readonly TrackingCoordinator _coordinator;
    private readonly ILogger _logger;

    public AutoTrackingService(GameDetectionService detection, TrackingCoordinator coordinator, ILogger<AutoTrackingService>? logger = null)
    {
        _detection = detection;
        _coordinator = coordinator;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _detection.GameStarted += OnGameStarted;
        _detection.GameStopped += OnGameStopped;
    }

    /// <summary>Completes when all start/stop work queued so far has finished (used by tests).</summary>
    public Task Idle
    {
        get
        {
            lock (_chainGate)
            {
                return _chain;
            }
        }
    }

    private readonly Lock _chainGate = new();
    private Task _chain = Task.CompletedTask;

    public void Dispose()
    {
        _detection.GameStarted -= OnGameStarted;
        _detection.GameStopped -= OnGameStopped;
    }

    private void OnGameStarted(object? sender, RunningGame e) =>
        Enqueue(() => _coordinator.StartTrackingAsync(e.Game), $"start {e.Game.Name}");

    private void OnGameStopped(object? sender, RunningGame e) =>
        Enqueue(() => _coordinator.StopTrackingAsync(e.Game.Id), $"stop {e.Game.Name}");

    /// <summary>Serializes lifecycle work so a quick stop/start pair is applied in order.</summary>
    private void Enqueue(Func<Task> work, string description)
    {
        lock (_chainGate)
        {
            _chain = _chain.ContinueWith(async _ =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-tracking failed to {Action}", description);
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }
}

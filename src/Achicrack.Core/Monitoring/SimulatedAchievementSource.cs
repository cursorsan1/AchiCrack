using Achicrack.Core.Models;

namespace Achicrack.Core.Monitoring;

/// <summary>
/// In-memory achievement source used for demos and tests. Behaves like a real monitor:
/// the initial state is delivered as a baseline, later <see cref="Unlock"/> calls as live snapshots.
/// </summary>
public sealed class SimulatedAchievementSource : IAchievementStateMonitor
{
    public const string SourceId = "simulated";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AchievementStateEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private SnapshotHandler? _handler;

    public SimulatedAchievementSource(IEnumerable<string>? initiallyUnlocked = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        foreach (var api in initiallyUnlocked ?? [])
        {
            _entries[api] = new AchievementStateEntry(api, true, _time.GetUtcNow());
        }
    }

    public string Source => SourceId;

    public IReadOnlyList<string> WatchedPaths => ["(simulated)"];

    public bool IsRunning => _handler is not null;

    public Task StartAsync(SnapshotHandler handler, CancellationToken cancellationToken = default)
    {
        _handler = handler;
        handler(Snapshot(), SnapshotKind.Baseline);
        return Task.CompletedTask;
    }

    /// <summary>Marks an achievement unlocked and delivers a live snapshot (if started).</summary>
    public void Unlock(string apiName, DateTimeOffset? at = null)
    {
        AchievementStateSnapshot snapshot;
        lock (_gate)
        {
            _entries[apiName] = new AchievementStateEntry(apiName, true, at ?? _time.GetUtcNow());
            snapshot = SnapshotLocked();
        }

        _handler?.Invoke(snapshot, SnapshotKind.Live);
    }

    public Task ReconcileNowAsync(CancellationToken cancellationToken = default)
    {
        _handler?.Invoke(Snapshot(), SnapshotKind.Live);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _handler = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _handler = null;
        return ValueTask.CompletedTask;
    }

    private AchievementStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    private AchievementStateSnapshot SnapshotLocked() =>
        new(new Dictionary<string, AchievementStateEntry>(_entries, StringComparer.Ordinal), []);
}

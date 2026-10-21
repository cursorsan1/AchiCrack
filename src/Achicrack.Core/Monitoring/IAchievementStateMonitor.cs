using Achicrack.Core.Models;

namespace Achicrack.Core.Monitoring;

/// <summary>Callback invoked serially by a monitor whenever it has a (new) snapshot.</summary>
public delegate void SnapshotHandler(AchievementStateSnapshot snapshot, SnapshotKind kind);

/// <summary>
/// Read-only observer of an achievement state source. Implementations must never write to the
/// observed source and must deliver snapshots serially (never concurrently).
/// </summary>
public interface IAchievementStateMonitor : IAsyncDisposable
{
    /// <summary>Source identifier recorded with unlocks (e.g. "goldberg").</summary>
    string Source { get; }

    /// <summary>Paths observed by this monitor (for diagnostics / UI).</summary>
    IReadOnlyList<string> WatchedPaths { get; }

    /// <summary>
    /// Reads the current state, delivers it as <see cref="SnapshotKind.Baseline"/>, then starts
    /// delivering <see cref="SnapshotKind.Live"/> snapshots for later changes.
    /// </summary>
    Task StartAsync(SnapshotHandler handler, CancellationToken cancellationToken = default);

    /// <summary>Forces an immediate re-read (e.g. right before stopping when the game exits).</summary>
    Task ReconcileNowAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

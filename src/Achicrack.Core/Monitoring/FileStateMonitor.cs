using Achicrack.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Monitoring;

public sealed class FileStateMonitorOptions
{
    /// <summary>Quiet period after a filesystem notification before the file is read.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Fallback reconciliation interval (cheap metadata check, re-read only on change).</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Attempts for a locked or half-written file before giving up until the next trigger.</summary>
    public int MaxReadAttempts { get; init; } = 5;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>State files larger than this are treated as malformed.</summary>
    public long MaxFileBytes { get; init; } = 16 * 1024 * 1024;
}

/// <summary>
/// Monitors one or more state files with <see cref="FileSystemWatcher"/> plus periodic reconciliation.
/// Handles atomic replacement (watching the directory, not the file handle), sharing violations,
/// incomplete writes (parse failures are retried and never replace the last good snapshot) and
/// files/directories that do not exist yet.
/// </summary>
public sealed class FileStateMonitor : IAchievementStateMonitor
{
    private readonly Func<string, AchievementStateSnapshot> _parse;
    private readonly FileStateMonitorOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<string, FileFingerprint?> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AchievementStateSnapshot> _lastGood = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SnapshotHandler? _handler;

    /// <param name="source">Source identifier recorded with unlocks.</param>
    /// <param name="paths">Candidate state files (union semantics).</param>
    /// <param name="parse">Parser; must throw <see cref="FormatException"/> for malformed or incomplete content.</param>
    public FileStateMonitor(
        string source,
        IReadOnlyList<string> paths,
        Func<string, AchievementStateSnapshot> parse,
        FileStateMonitorOptions? options = null,
        ILogger? logger = null)
    {
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one state path is required.", nameof(paths));
        }

        Source = source;
        WatchedPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _parse = parse;
        _options = options ?? new FileStateMonitorOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public string Source { get; }

    public IReadOnlyList<string> WatchedPaths { get; }

    /// <summary>Number of times a read failed and the last good snapshot was kept (diagnostics/tests).</summary>
    public int FailedReadCount { get; private set; }

    public async Task StartAsync(SnapshotHandler handler, CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("Monitor already started.");
        }

        _handler = handler;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ArmWatchers();

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadChangedAsync(force: true, _cts.Token).ConfigureAwait(false);
            Deliver(Merged(), SnapshotKind.Baseline);
        }
        finally
        {
            _readGate.Release();
        }

        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Monitoring {Paths}", string.Join("; ", WatchedPaths));
    }

    public async Task ReconcileNowAsync(CancellationToken cancellationToken = default)
    {
        if (_handler is null)
        {
            return;
        }

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ReadChangedAsync(force: true, cancellationToken).ConfigureAwait(false))
            {
                Deliver(Merged(), SnapshotKind.Live);
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        lock (_watchers)
        {
            foreach (var w in _watchers.Values)
            {
                w.Dispose();
            }

            _watchers.Clear();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loop = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool signalled;
            try
            {
                signalled = await _signal.WaitAsync(_options.ReconcileInterval, ct).ConfigureAwait(false);
                if (signalled)
                {
                    // Debounce bursts (truncate + write + close produce several notifications).
                    await Task.Delay(_options.Debounce, ct).ConfigureAwait(false);
                    while (_signal.CurrentCount > 0)
                    {
                        await _signal.WaitAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                ArmWatchers();
                await _readGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (await ReadChangedAsync(force: signalled, ct).ConfigureAwait(false))
                    {
                        Deliver(Merged(), SnapshotKind.Live);
                    }
                }
                finally
                {
                    _readGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in state monitor loop for {Paths}", string.Join("; ", WatchedPaths));
            }
        }
    }

    /// <returns>True when at least one file produced a new good snapshot.</returns>
    private async Task<bool> ReadChangedAsync(bool force, CancellationToken ct)
    {
        var changed = false;
        foreach (var path in WatchedPaths)
        {
            var fingerprint = FileFingerprint.Of(path);
            _fingerprints.TryGetValue(path, out var previous);
            if (fingerprint is null)
            {
                // File absent (not created yet, or being atomically replaced). Keep the last good snapshot.
                _fingerprints[path] = null;
                continue;
            }

            if (!force && previous == fingerprint)
            {
                continue;
            }

            var snapshot = await TryReadAsync(path, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                FailedReadCount++;
                continue; // fingerprint not updated → the next reconciliation retries
            }

            _fingerprints[path] = FileFingerprint.Of(path) ?? fingerprint;
            _lastGood[path] = snapshot;
            changed = true;
        }

        return changed;
    }

    private async Task<AchievementStateSnapshot?> TryReadAsync(string path, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= _options.MaxReadAttempts; attempt++)
        {
            try
            {
                var text = ReadShared(path);
                return _parse(text);
            }
            catch (FileNotFoundException ex)
            {
                last = ex;
            }
            catch (DirectoryNotFoundException ex)
            {
                last = ex;
            }
            catch (IOException ex)
            {
                last = ex; // sharing violation or similar
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }
            catch (FormatException ex)
            {
                last = ex; // incomplete write or corrupt file
            }

            await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
        }

        _logger.LogWarning(last, "Could not read state file {Path} after {Attempts} attempts; keeping last good state",
            path, _options.MaxReadAttempts);
        return null;
    }

    private string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > _options.MaxFileBytes)
        {
            throw new FormatException($"State file is unexpectedly large ({stream.Length} bytes).");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private AchievementStateSnapshot Merged() => AchievementStateSnapshot.Merge(_lastGood.Values);

    private void Deliver(AchievementStateSnapshot snapshot, SnapshotKind kind)
    {
        try
        {
            _handler?.Invoke(snapshot, kind);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot handler failed ({Kind})", kind);
        }
    }

    private void ArmWatchers()
    {
        lock (_watchers)
        {
            ArmWatchersLocked();
        }
    }

    private void ArmWatchersLocked()
    {
        foreach (var path in WatchedPaths)
        {
            var dir = Path.GetDirectoryName(path)!;
            var key = path;
            if (_watchers.ContainsKey(key) || !Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size |
                                   NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += (_, _) => Signal();
                watcher.Created += (_, _) => Signal();
                watcher.Deleted += (_, _) => Signal();
                watcher.Renamed += (_, _) => Signal();
                watcher.Error += (_, e) =>
                {
                    _logger.LogWarning(e.GetException(), "FileSystemWatcher error for {Path}; re-arming", key);
                    FileSystemWatcher? broken;
                    lock (_watchers)
                    {
                        _watchers.Remove(key, out broken);
                    }

                    broken?.Dispose();

                    Signal();
                };
                watcher.EnableRaisingEvents = true;
                _watchers[key] = watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not watch {Directory}; relying on periodic reconciliation", dir);
            }
        }
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private sealed record FileFingerprint(long Length, DateTime LastWriteUtc)
    {
        public static FileFingerprint? Of(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileFingerprint(info.Length, info.LastWriteTimeUtc) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}

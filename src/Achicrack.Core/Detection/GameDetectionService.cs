using Achicrack.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Detection;

public sealed class GameDetectionOptions
{
    /// <summary>Process list polling interval. Enumeration is cheap; paths are only queried for new processes.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a game may have no matching process before it counts as stopped. Absorbs restarts
    /// (launcher → game handoff, crash-reporter relaunch) without duplicate start notifications.
    /// </summary>
    public TimeSpan ExitGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A registered game with at least one matching process.</summary>
public sealed class RunningGame
{
    internal RunningGame(GameRecord game, DateTimeOffset startedAt)
    {
        Game = game;
        StartedAtUtc = startedAt;
    }

    public GameRecord Game { get; internal set; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? LastForegroundUtc { get; internal set; }

    internal HashSet<ProcessKey> Processes { get; } = [];

    internal DateTimeOffset? PendingExitSinceUtc { get; set; }

    public IReadOnlyList<int> ProcessIds => Processes.Select(p => p.ProcessId).ToArray();
}

public sealed record UnmatchedProcess(ProcessInfo Process, string? ExecutablePath, string Reason);

/// <summary>
/// Polls the process list and turns process starts/exits into per-game start/stop events.
/// Detection runs off the UI thread; events are raised on the polling thread.
/// </summary>
public sealed class GameDetectionService
{
    private readonly IProcessProvider _processes;
    private readonly IForegroundProcessProvider? _foreground;
    private readonly Func<IReadOnlyList<GameRecord>> _gamesProvider;
    private readonly GameDetectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    /// <summary>Every process seen in the last poll → matched game id (null = not a registered game).</summary>
    private readonly Dictionary<ProcessKey, long?> _known = [];
    private readonly Dictionary<long, RunningGame> _running = [];
    private IReadOnlyList<GameRecord> _games = [];
    private bool _gamesDirty = true;
    private long? _primaryGameId;

    public GameDetectionService(
        IProcessProvider processes,
        Func<IReadOnlyList<GameRecord>> games,
        IForegroundProcessProvider? foreground = null,
        GameDetectionOptions? options = null,
        TimeProvider? time = null,
        ILogger<GameDetectionService>? logger = null)
    {
        _processes = processes;
        _gamesProvider = games;
        _foreground = foreground;
        _options = options ?? new GameDetectionOptions();
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler<RunningGame>? GameStarted;

    public event EventHandler<RunningGame>? GameStopped;

    public event EventHandler<long?>? PrimaryGameChanged;

    /// <summary>Raised when a process could have been a game but was not identified confidently.</summary>
    public event EventHandler<UnmatchedProcess>? AmbiguousProcess;

    public long? PrimaryGameId
    {
        get
        {
            lock (_gate)
            {
                return _primaryGameId;
            }
        }
    }

    public IReadOnlyList<RunningGame> RunningGames
    {
        get
        {
            lock (_gate)
            {
                return _running.Values.ToArray();
            }
        }
    }

    /// <summary>Call after games were added, edited or removed so running processes are re-matched.</summary>
    public void InvalidateGames()
    {
        lock (_gate)
        {
            _gamesDirty = true;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Process poll failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Performs one detection pass (public for tests and manual refresh).</summary>
    public void PollOnce()
    {
        var started = new List<RunningGame>();
        var stopped = new List<RunningGame>();
        var ambiguous = new List<UnmatchedProcess>();
        long? primaryChangedTo = null;
        var primaryChanged = false;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_gamesDirty)
            {
                _games = _gamesProvider();
                _gamesDirty = false;
                _known.Clear(); // re-match everything against the new registrations
                foreach (var r in _running.Values.ToArray())
                {
                    var updated = _games.FirstOrDefault(g => g.Id == r.Game.Id && g.Enabled);
                    if (updated is null)
                    {
                        _running.Remove(r.Game.Id);
                        stopped.Add(r);
                    }
                    else
                    {
                        r.Game = updated;
                        r.Processes.Clear();
                        r.PendingExitSinceUtc ??= now;
                    }
                }
            }

            var snapshot = _processes.GetProcesses();
            var current = new HashSet<ProcessKey>(snapshot.Select(ProcessKey.From));

            // Exits
            foreach (var key in _known.Keys.Where(k => !current.Contains(k)).ToArray())
            {
                var gameId = _known[key];
                _known.Remove(key);
                if (gameId is { } id && _running.TryGetValue(id, out var r) && r.Processes.Remove(key) && r.Processes.Count == 0)
                {
                    r.PendingExitSinceUtc = now;
                }
            }

            // Starts
            foreach (var process in snapshot)
            {
                var key = ProcessKey.From(process);
                if (_known.ContainsKey(key))
                {
                    continue;
                }

                long? matchedId = null;
                if (_games.Count > 0)
                {
                    var path = _processes.TryGetExecutablePath(process.ProcessId);
                    var match = GameProcessMatcher.Match(path, _games);
                    if (match.IsMatch)
                    {
                        matchedId = match.Game!.Id;
                        if (!_running.TryGetValue(matchedId.Value, out var r))
                        {
                            r = new RunningGame(match.Game, now);
                            _running[matchedId.Value] = r;
                            started.Add(r);
                            _logger.LogInformation("Game started: {Game} (pid {Pid}, {Reason})", match.Game.Name,
                                process.ProcessId, match.Reason);
                        }

                        r.Processes.Add(key);
                        r.PendingExitSinceUtc = null;
                    }
                    else if (match.Confidence == MatchConfidence.Ambiguous)
                    {
                        ambiguous.Add(new UnmatchedProcess(process, path, match.Reason));
                    }
                }

                _known[key] = matchedId;
            }

            // Grace-period expiry
            foreach (var r in _running.Values.ToArray())
            {
                if (r.PendingExitSinceUtc is { } since && now - since >= _options.ExitGracePeriod)
                {
                    _running.Remove(r.Game.Id);
                    stopped.Add(r);
                    _logger.LogInformation("Game stopped: {Game}", r.Game.Name);
                }
            }

            // Primary ("currently playing") selection
            var newPrimary = SelectPrimary(now);
            if (newPrimary != _primaryGameId)
            {
                _primaryGameId = newPrimary;
                primaryChanged = true;
                primaryChangedTo = newPrimary;
            }
        }

        foreach (var s in stopped)
        {
            Raise(GameStopped, s);
        }

        foreach (var s in started)
        {
            Raise(GameStarted, s);
        }

        foreach (var a in ambiguous)
        {
            Raise(AmbiguousProcess, a);
        }

        if (primaryChanged)
        {
            Raise(PrimaryGameChanged, primaryChangedTo);
        }
    }

    private long? SelectPrimary(DateTimeOffset now)
    {
        if (_running.Count == 0)
        {
            return null;
        }

        var fgPid = _foreground?.GetForegroundProcessId();
        if (fgPid is { } pid)
        {
            var fgGame = _running.Values.FirstOrDefault(r => r.Processes.Any(p => p.ProcessId == pid));
            if (fgGame is not null)
            {
                fgGame.LastForegroundUtc = now;
                return fgGame.Game.Id;
            }
        }

        // Keep the current primary while it is still running (switching windows must not flip it).
        if (_primaryGameId is { } current && _running.ContainsKey(current))
        {
            return current;
        }

        return _running.Values
            .OrderByDescending(r => r.LastForegroundUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.StartedAtUtc)
            .First().Game.Id;
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detection event handler failed");
        }
    }
}

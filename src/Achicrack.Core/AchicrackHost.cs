using Achicrack.Core.Adapters;
using Achicrack.Core.Adapters.Goldberg;
using Achicrack.Core.Adapters.Simulated;
using Achicrack.Core.Catalog;
using Achicrack.Core.Detection;
using Achicrack.Core.Events;
using Achicrack.Core.Infrastructure;
using Achicrack.Core.Models;
using Achicrack.Core.Monitoring;
using Achicrack.Core.Notifications;
using Achicrack.Core.Persistence;
using Achicrack.Core.Scanning;
using Achicrack.Core.Settings;
using Achicrack.Core.Tracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core;

/// <summary>Platform services the host needs from the UI/OS layer.</summary>
public sealed record HostPlatform
{
    public required INotificationPresenter Presenter { get; init; }
    public required IProcessProvider Processes { get; init; }
    public IForegroundProcessProvider? Foreground { get; init; }
    public IUserEnvironment Environment { get; init; } = new SystemUserEnvironment();
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public HttpClient? Http { get; init; }
}

/// <summary>
/// Composition root of the achievement engine. UI-agnostic so the same wiring is exercised by
/// the WPF app and by end-to-end tests.
/// </summary>
public sealed class AchicrackHost : IAsyncDisposable
{
    private readonly HostPlatform _platform;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _detectionLoop;

    public AchicrackHost(AppPaths paths, SettingsStore settings, HostPlatform platform)
    {
        Paths = paths;
        Settings = settings;
        _platform = platform;
        var lf = platform.LoggerFactory;
        _logger = lf.CreateLogger<AchicrackHost>();
        var time = platform.Time;

        paths.EnsureCreated();
        Database = new AchicrackDatabase(settings.Current.DatabasePathOverride ?? paths.DefaultDatabasePath);
        Games = new GameRepository(Database);
        Achievements = new AchievementRepository(Database);

        Goldberg = new GoldbergAdapter(platform.Environment);
        Simulated = new SimulatedGameAdapter();
        Adapters = new AdapterRegistry([Goldberg, Simulated]);

        IconCache = new IconCache(paths.IconCacheDirectory, lf.CreateLogger<IconCache>());
        var online = platform.Http is not null ? new SteamGlobalRarityProvider(platform.Http) : null;
        Catalogs = new AchievementCatalogService(Adapters, Achievements, IconCache, () => settings.Current.Language,
            online, lf.CreateLogger<AchievementCatalogService>());

        Engine = new AchievementEventEngine(Achievements, Catalogs, time, lf.CreateLogger<AchievementEventEngine>());
        Coordinator = new TrackingCoordinator(Adapters, Engine, Catalogs, Games, MonitorOptions, time,
            lf.CreateLogger<TrackingCoordinator>());

        var monitoring = settings.Current.Monitoring;
        Detection = new GameDetectionService(platform.Processes, () => Games.GetAll(), platform.Foreground,
            new GameDetectionOptions
            {
                PollInterval = TimeSpan.FromSeconds(Math.Clamp(monitoring.ProcessPollSeconds, 0.5, 30)),
                ExitGracePeriod = TimeSpan.FromSeconds(Math.Clamp(monitoring.ExitGraceSeconds, 0, 120)),
            }, time, lf.CreateLogger<GameDetectionService>());
        AutoTracking = new AutoTrackingService(Detection, Coordinator, lf.CreateLogger<AutoTrackingService>());

        Queue = new NotificationQueue(platform.Presenter, new NotificationQueueOptions(), time,
            lf.CreateLogger<NotificationQueue>());
        Router = new NotificationRouter(Engine, Coordinator, Queue, Achievements, () => settings.Current, time,
            lf.CreateLogger<NotificationRouter>());

        Scanner = new GameScanner(Adapters);
        Registration = new GameRegistrationService(Games, time);
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public AchicrackDatabase Database { get; }
    public GameRepository Games { get; }
    public AchievementRepository Achievements { get; }
    public GoldbergAdapter Goldberg { get; }
    public SimulatedGameAdapter Simulated { get; }
    public AdapterRegistry Adapters { get; }
    public IconCache IconCache { get; }
    public AchievementCatalogService Catalogs { get; }
    public AchievementEventEngine Engine { get; }
    public TrackingCoordinator Coordinator { get; }
    public GameDetectionService Detection { get; }
    public AutoTrackingService AutoTracking { get; }
    public NotificationQueue Queue { get; }
    public NotificationRouter Router { get; }
    public GameScanner Scanner { get; }
    public GameRegistrationService Registration { get; }

    /// <summary>Initializes the database and starts the notification queue and (optionally) process detection.</summary>
    public void Start(bool startDetectionLoop = true)
    {
        Database.Initialize();
        Queue.Start();
        _cts = new CancellationTokenSource();
        if (startDetectionLoop && Settings.Current.AutoDetectGames)
        {
            _detectionLoop = Task.Run(() => Detection.RunAsync(_cts.Token));
        }

        _logger.LogInformation("Achicrack engine started (db {Db}, schema v{Schema})", Database.DatabasePath, Database.SchemaVersion);
    }

    /// <summary>Creates (once) the built-in demo game used for simulated unlocks.</summary>
    public GameRecord EnsureDemoGame()
    {
        var existing = Games.GetAll().FirstOrDefault(g => g.AdapterId == SimulatedGameAdapter.AdapterId);
        if (existing is not null)
        {
            return existing;
        }

        var game = new GameRecord
        {
            Name = SimulatedGameAdapter.DemoGameName,
            InstallDirectory = Path.Combine(Paths.DataDirectory, "demo"),
            AdapterId = SimulatedGameAdapter.AdapterId,
            AddedAtUtc = _platform.Time.GetUtcNow(),
        };
        Games.Add(game);
        Detection.InvalidateGames();
        return game;
    }

    /// <summary>Simulates an unlock of the next locked demo achievement through the full pipeline.</summary>
    public async Task<string?> SimulateDemoUnlockAsync(CancellationToken cancellationToken = default)
    {
        var game = EnsureDemoGame();
        if (Coordinator.GetSession(game.Id)?.State != TrackingState.TrackingActive)
        {
            await Coordinator.StartTrackingAsync(game, cancellationToken).ConfigureAwait(false);
        }

        var unlocked = Achievements.GetUnlocks(game.Id);
        var next = SimulatedGameAdapter.Definitions.FirstOrDefault(d => !unlocked.ContainsKey(d.ApiName));
        if (next is null)
        {
            return null;
        }

        Simulated.GetSource(game.Id)?.Unlock(next.ApiName);
        return next.ApiName;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_detectionLoop is not null)
        {
            await _detectionLoop.ConfigureAwait(false);
        }

        AutoTracking.Dispose();
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        Router.Dispose();
        await Queue.StopAsync().ConfigureAwait(false);
        _logger.LogInformation("Achicrack engine stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private FileStateMonitorOptions MonitorOptions()
    {
        var m = Settings.Current.Monitoring;
        return new FileStateMonitorOptions
        {
            ReconcileInterval = TimeSpan.FromSeconds(Math.Clamp(m.ReconcileSeconds, 1, 300)),
            Debounce = TimeSpan.FromMilliseconds(Math.Clamp(m.DebounceMilliseconds, 50, 5000)),
        };
    }
}

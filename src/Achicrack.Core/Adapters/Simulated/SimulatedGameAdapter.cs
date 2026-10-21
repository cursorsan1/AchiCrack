using System.Collections.Concurrent;
using Achicrack.Core.Models;
using Achicrack.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Achicrack.Core.Adapters.Simulated;

/// <summary>
/// Adapter for the built-in demo game. It exercises the full pipeline (event engine, database,
/// notifications, dashboard) without any real game. Its achievements are Achicrack's own demo data.
/// </summary>
public sealed class SimulatedGameAdapter : IGameAdapter
{
    public const string AdapterId = "simulated";
    public const string DemoGameName = "Achicrack Demo";

    private static readonly AchievementDefinition[] DemoDefinitions =
    [
        new() { ApiName = "DEMO_FIRST_STEPS", DisplayName = "First Steps", Description = "Trigger your first simulated unlock.", SortOrder = 0, GlobalPercent = 72.5 },
        new() { ApiName = "DEMO_COLLECTOR", DisplayName = "Collector", Description = "Unlock three demo achievements.", SortOrder = 1, GlobalPercent = 41.0 },
        new() { ApiName = "DEMO_NIGHT_OWL", DisplayName = "Night Owl", Description = "Simulate an unlock after midnight.", SortOrder = 2, GlobalPercent = 18.2 },
        new() { ApiName = "DEMO_PERFECTIONIST", DisplayName = "Perfectionist", Description = "Customize the notification in Notification Studio.", SortOrder = 3, GlobalPercent = 9.7 },
        new() { ApiName = "DEMO_SPEEDRUN", DisplayName = "Speedrunner", Description = "Unlock two demo achievements within ten seconds.", SortOrder = 4, GlobalPercent = 3.1 },
        new() { ApiName = "DEMO_SECRET", DisplayName = "Hidden Depths", Description = "A hidden demo achievement.", Hidden = true, SortOrder = 5, GlobalPercent = 0.8 },
    ];

    private readonly ConcurrentDictionary<long, SimulatedAchievementSource> _sources = new();

    public string Id => AdapterId;

    public string DisplayName => "Simulated (demo)";

    public static IReadOnlyList<AchievementDefinition> Definitions => DemoDefinitions;

    public AdapterProbeResult? Probe(string steamApiDirectory) => null;

    public IReadOnlyList<string> GetStatePaths(GameRecord game) => [];

    public IAchievementStateMonitor CreateMonitor(GameRecord game, FileStateMonitorOptions options, ILogger logger) =>
        _sources.GetOrAdd(game.Id, _ => new SimulatedAchievementSource());

    public AchievementCatalog? LoadCatalog(GameRecord game, string language, ILogger logger) => new()
    {
        Definitions = DemoDefinitions,
        IsComplete = true,
        Source = "Built-in demo catalog",
        RetrievedAtUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>Returns the live source for a demo game, or null if it is not being tracked.</summary>
    public SimulatedAchievementSource? GetSource(long gameId) => _sources.GetValueOrDefault(gameId);
}

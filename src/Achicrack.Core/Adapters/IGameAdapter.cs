using Achicrack.Core.Models;
using Achicrack.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Achicrack.Core.Adapters;

/// <summary>How confident Achicrack is that a detection method works for a game.</summary>
public enum DetectionStatus
{
    /// <summary>A verified, implemented read-only method applies.</summary>
    Supported,

    /// <summary>Indicators exist, but support could not be confirmed (no markers, untested layout...).</summary>
    Unverified,

    /// <summary>The method does not apply or is not implemented.</summary>
    Unsupported,
}

/// <summary>Result of an adapter examining a folder that contains a steam_api DLL.</summary>
public sealed record AdapterProbeResult
{
    public required string AdapterId { get; init; }
    public required string MethodName { get; init; }
    public required DetectionStatus Status { get; init; }
    public required string Details { get; init; }
    public required string SteamApiDirectory { get; init; }
    public string? VariantName { get; init; }
    public uint? AppId { get; init; }
    public string? AppIdSource { get; init; }
    public IReadOnlyList<string> StatePaths { get; init; } = [];
    public string? CatalogPath { get; init; }
}

/// <summary>Environment values adapters depend on (abstracted for tests).</summary>
public interface IUserEnvironment
{
    /// <summary>%APPDATA% (roaming application data).</summary>
    string RoamingAppData { get; }
}

public sealed class SystemUserEnvironment : IUserEnvironment
{
    public string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
}

/// <summary>
/// A read-only integration with one Steam API implementation. Adapters translate implementation
/// specific files into normalized snapshots and catalogs; they never write into game folders.
/// </summary>
public interface IGameAdapter
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Examines a folder containing steam_api(64).dll. Returns null when not applicable.</summary>
    AdapterProbeResult? Probe(string steamApiDirectory);

    /// <summary>Candidate state files for a registered game.</summary>
    IReadOnlyList<string> GetStatePaths(GameRecord game);

    IAchievementStateMonitor CreateMonitor(GameRecord game, FileStateMonitorOptions options, ILogger logger);

    /// <summary>Loads achievement definitions from local metadata, or null when none exist.</summary>
    AchievementCatalog? LoadCatalog(GameRecord game, string language, ILogger logger);
}

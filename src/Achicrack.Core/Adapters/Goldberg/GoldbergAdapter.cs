using Achicrack.Core.Models;
using Achicrack.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Achicrack.Core.Adapters.Goldberg;

/// <summary>
/// Read-only adapter for Goldberg Steam Emulator and gbe_fork (GSE).
///
/// Known save locations (per app id):
///   classic Goldberg : %APPDATA%\Goldberg SteamEmu Saves\&lt;appid&gt;\achievements.json
///   gbe_fork (GSE)   : %APPDATA%\GSE Saves\&lt;appid&gt;\achievements.json
///   portable         : classic local_save.txt / gbe_fork configs.user.ini [user::saves] local_save_path
///                      (portable layouts are implemented from documentation and marked unverified)
/// </summary>
public sealed class GoldbergAdapter(IUserEnvironment environment) : IGameAdapter
{
    public const string AdapterId = "goldberg";
    public const string ClassicSavesFolder = "Goldberg SteamEmu Saves";
    public const string GseSavesFolder = "GSE Saves";
    public const string StateFileName = "achievements.json";
    public const string SettingsFolder = "steam_settings";

    public string Id => AdapterId;

    public string DisplayName => "Goldberg Steam Emulator / GSE (gbe_fork)";

    public AdapterProbeResult? Probe(string steamApiDirectory)
    {
        var settingsDir = Path.Combine(steamApiDirectory, SettingsFolder);
        var hasSettings = Directory.Exists(settingsDir);
        var hasInterfacesFile = File.Exists(Path.Combine(steamApiDirectory, "steam_interfaces.txt"));
        var isGse = hasSettings && Directory.EnumerateFiles(settingsDir, "configs.*.ini").Any();
        var hasLocalSave = File.Exists(Path.Combine(steamApiDirectory, "local_save.txt"));

        var (appId, appIdSource) = SteamAppIdResolver.Resolve(steamApiDirectory);
        var variant = isGse ? "GSE (gbe_fork)" : hasSettings || hasInterfacesFile ? "Goldberg (classic or fork)" : null;
        var catalogPath = Path.Combine(settingsDir, GoldbergCatalogParser.ManifestFileName);
        var statePaths = appId is { } id ? ResolveStatePaths(steamApiDirectory, id, out _) : [];

        DetectionStatus status;
        string details;
        if (variant is null)
        {
            status = DetectionStatus.Unverified;
            details = "steam_api DLL found, but no Goldberg/GSE markers (steam_settings, steam_interfaces.txt). " +
                      "This may be the genuine Steam API, which Achicrack cannot monitor offline.";
        }
        else if (appId is null)
        {
            status = DetectionStatus.Unverified;
            details = $"{variant} markers found, but no steam_appid.txt — the save folder cannot be located. " +
                      "Enter the App ID manually to enable monitoring.";
        }
        else
        {
            ResolveStatePaths(steamApiDirectory, appId.Value, out var portable);
            status = portable ? DetectionStatus.Unverified : DetectionStatus.Supported;
            details = portable
                ? $"{variant} with a portable save path. Implemented from documentation; not yet validated with a real game."
                : $"{variant} detected. Achievement state is read from the emulator's achievements.json (read-only).";
        }

        if (hasLocalSave && variant is not null)
        {
            details += " local_save.txt present.";
        }

        return new AdapterProbeResult
        {
            AdapterId = AdapterId,
            MethodName = "Local achievement state file (Goldberg / GSE)",
            Status = status,
            Details = details,
            SteamApiDirectory = steamApiDirectory,
            VariantName = variant,
            AppId = appId,
            AppIdSource = appIdSource,
            StatePaths = statePaths,
            CatalogPath = File.Exists(catalogPath) ? catalogPath : null,
        };
    }

    public IReadOnlyList<string> GetStatePaths(GameRecord game)
    {
        if (game.AppId is not { } appId)
        {
            return [];
        }

        return ResolveStatePaths(game.SteamApiDirectory ?? game.InstallDirectory, appId, out _);
    }

    public IAchievementStateMonitor CreateMonitor(GameRecord game, FileStateMonitorOptions options, ILogger logger)
    {
        var paths = GetStatePaths(game);
        if (paths.Count == 0)
        {
            throw new InvalidOperationException($"No App ID for {game.Name}; cannot locate the Goldberg save folder.");
        }

        return new FileStateMonitor(AdapterId, paths, GoldbergStateParser.Parse, options, logger);
    }

    public AchievementCatalog? LoadCatalog(GameRecord game, string language, ILogger logger)
    {
        var settingsDir = Path.Combine(game.SteamApiDirectory ?? game.InstallDirectory, SettingsFolder);
        var manifest = Path.Combine(settingsDir, GoldbergCatalogParser.ManifestFileName);
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            var warnings = new List<string>();
            var defs = GoldbergCatalogParser.Parse(File.ReadAllText(manifest), settingsDir, language, warnings);
            foreach (var w in warnings)
            {
                logger.LogWarning("Manifest {Path}: {Warning}", manifest, w);
            }

            return new AchievementCatalog
            {
                Definitions = defs,
                // The manifest is generated from the game's Steam schema and lists every achievement.
                IsComplete = defs.Count > 0,
                Source = manifest,
                Notes = warnings.Count > 0 ? $"{warnings.Count} manifest warning(s)" : null,
                RetrievedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read achievement manifest {Path}", manifest);
            return null;
        }
    }

    /// <summary>Ordered candidate state files. <paramref name="portable"/> is true when a portable layout is configured.</summary>
    internal IReadOnlyList<string> ResolveStatePaths(string steamApiDirectory, uint appId, out bool portable)
    {
        var app = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = new List<string>();
        portable = false;

        // gbe_fork: steam_settings\configs.user.ini → [user::saves] local_save_path / saves_folder_name
        var ini = IniFile.TryLoad(Path.Combine(steamApiDirectory, SettingsFolder, "configs.user.ini"));
        var savesFolderName = GseSavesFolder;
        if (ini is not null)
        {
            if (ini.Get("user::saves", "local_save_path") is { Length: > 0 } localSave)
            {
                var root = Path.GetFullPath(Path.Combine(steamApiDirectory, localSave));
                result.Add(Path.Combine(root, app, StateFileName));
                result.Add(Path.Combine(root, StateFileName));
                portable = true;
            }

            if (ini.Get("user::saves", "saves_folder_name") is { Length: > 0 } custom)
            {
                savesFolderName = custom;
            }
        }

        // Classic Goldberg portable mode: local_save.txt next to the DLL names a folder relative to it.
        var localSaveTxt = Path.Combine(steamApiDirectory, "local_save.txt");
        if (File.Exists(localSaveTxt))
        {
            string? folder = null;
            try
            {
                folder = File.ReadAllText(localSaveTxt).Trim();
            }
            catch (IOException)
            {
            }

            if (!string.IsNullOrEmpty(folder) && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            {
                var root = Path.GetFullPath(Path.Combine(steamApiDirectory, folder));
                result.Add(Path.Combine(root, app, StateFileName));
                portable = true;
            }
        }

        var appData = environment.RoamingAppData;
        result.Add(Path.Combine(appData, savesFolderName, app, StateFileName));
        if (!string.Equals(savesFolderName, GseSavesFolder, StringComparison.OrdinalIgnoreCase))
        {
            result.Add(Path.Combine(appData, GseSavesFolder, app, StateFileName));
        }

        result.Add(Path.Combine(appData, ClassicSavesFolder, app, StateFileName));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

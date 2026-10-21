namespace Achicrack.Core.Infrastructure;

/// <summary>Locations of Achicrack's own data. Nothing is ever written into game folders.</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <summary>Default: %LOCALAPPDATA%\Achicrack.</summary>
    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Achicrack"));

    public string DataDirectory { get; }

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string DefaultDatabasePath => Path.Combine(DataDirectory, "achicrack.db");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string IconCacheDirectory => Path.Combine(DataDirectory, "cache", "icons");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
    }
}

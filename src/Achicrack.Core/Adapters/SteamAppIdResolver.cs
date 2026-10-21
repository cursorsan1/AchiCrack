using System.Globalization;

namespace Achicrack.Core.Adapters;

/// <summary>Resolves a Steam App ID from local files only (steam_appid.txt in its usual locations).</summary>
public static class SteamAppIdResolver
{
    public const string AppIdFileName = "steam_appid.txt";

    public static (uint? AppId, string? Source) Resolve(string steamApiDirectory, IEnumerable<string>? extraDirectories = null)
    {
        var candidates = new List<string>
        {
            Path.Combine(steamApiDirectory, AppIdFileName),
            Path.Combine(steamApiDirectory, "steam_settings", AppIdFileName),
        };
        candidates.AddRange((extraDirectories ?? []).Select(d => Path.Combine(d, AppIdFileName)));

        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var id = TryReadAppIdFile(file);
            if (id is not null)
            {
                return (id, file);
            }
        }

        return (null, null);
    }

    public static uint? TryReadAppIdFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024)
            {
                return null;
            }

            return ParseAppId(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Accepts the first token of the file (some files contain trailing whitespace or comments).</summary>
    public static uint? ParseAppId(string text)
    {
        var token = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is not null && uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }
}

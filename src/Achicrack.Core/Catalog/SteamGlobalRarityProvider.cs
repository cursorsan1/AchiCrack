using System.Globalization;
using System.Text.Json;

namespace Achicrack.Core.Catalog;

/// <summary>
/// Optional rarity source: Steam Web API ISteamUserStats/GetGlobalAchievementPercentagesForApp,
/// a public endpoint that needs no API key or Steam login. Only used when enabled in settings.
/// </summary>
public sealed class SteamGlobalRarityProvider(HttpClient http) : IOnlineMetadataProvider
{
    public async Task<IReadOnlyDictionary<string, double>?> GetGlobalPercentagesAsync(uint appId, CancellationToken cancellationToken)
    {
        var url = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}";
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    internal static IReadOnlyDictionary<string, double>? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("achievementpercentages", out var root) ||
            !root.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name ||
                !item.TryGetProperty("percent", out var p))
            {
                continue;
            }

            double? percent = p.ValueKind switch
            {
                JsonValueKind.Number => p.GetDouble(),
                JsonValueKind.String when double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            };
            if (percent is { } value and >= 0 and <= 100)
            {
                result[name] = value;
            }
        }

        return result;
    }
}

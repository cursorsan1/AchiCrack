using System.Globalization;
using System.Text.Json;
using Achicrack.Core.Models;

namespace Achicrack.Core.Adapters.Goldberg;

/// <summary>
/// Parses the achievement definitions manifest used by Goldberg-family emulators:
/// <c>steam_settings\achievements.json</c> — a JSON array generated from the game's Steam schema.
/// <code>
/// [ { "name": "ACH_X", "displayName": "Title" | { "english": "Title", ... },
///     "description": "..." | { ... }, "hidden": "0", "icon": "images/x.jpg", "icon_gray": "images/x_gray.jpg" } ]
/// </code>
/// </summary>
public static class GoldbergCatalogParser
{
    public const string ManifestFileName = "achievements.json";

    private static readonly string[] IconSubfolders = ["", "images", "achievement_images", "achievements"];

    /// <exception cref="FormatException">Not a JSON array.</exception>
    public static IReadOnlyList<AchievementDefinition> Parse(string json, string steamSettingsDirectory, string language,
        List<string>? warnings = null)
    {
        warnings ??= [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException("Achievement manifest is not valid JSON.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Achievement manifest root must be an array.");
            }

            var result = new List<AchievementDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var order = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add("Skipped non-object manifest entry.");
                    continue;
                }

                var api = ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(api))
                {
                    warnings.Add("Skipped manifest entry without 'name'.");
                    continue;
                }

                if (!seen.Add(api))
                {
                    warnings.Add($"Duplicate manifest entry '{api}' ignored.");
                    continue;
                }

                result.Add(new AchievementDefinition
                {
                    ApiName = api,
                    DisplayName = NullIfBlank(ReadLocalized(item, "displayName", language)),
                    Description = NullIfBlank(ReadLocalized(item, "description", language)),
                    Hidden = ReadHidden(item),
                    IconPath = ResolveIcon(steamSettingsDirectory, ReadString(item, "icon")),
                    IconGrayPath = ResolveIcon(steamSettingsDirectory,
                        ReadString(item, "icon_gray") ?? ReadString(item, "icongray")),
                    SortOrder = order++,
                });
            }

            return result;
        }
    }

    internal static string? ResolveIcon(string steamSettingsDirectory, string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        if (icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return icon;
        }

        var normalized = icon.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return File.Exists(normalized) ? normalized : null;
        }

        foreach (var sub in IconSubfolders)
        {
            var candidate = Path.GetFullPath(Path.Combine(steamSettingsDirectory, sub, normalized));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ReadLocalized(JsonElement obj, string name, string language)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        if (v.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var lang in new[] { language, "english" })
        {
            if (v.TryGetProperty(lang, out var localized) && localized.ValueKind == JsonValueKind.String)
            {
                return localized.GetString();
            }
        }

        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                return p.Value.GetString();
            }
        }

        return null;
    }

    private static bool ReadHidden(JsonElement obj)
    {
        if (!obj.TryGetProperty("hidden", out var v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetInt64(out var n) && n != 0,
            JsonValueKind.String => v.GetString() is { } s &&
                                    (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                     (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ns) && ns != 0)),
            _ => false,
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

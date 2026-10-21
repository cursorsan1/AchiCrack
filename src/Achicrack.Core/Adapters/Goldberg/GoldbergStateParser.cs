using System.Globalization;
using System.Text.Json;
using Achicrack.Core.Models;

namespace Achicrack.Core.Adapters.Goldberg;

/// <summary>
/// Parses the per-app achievement state file written by Goldberg Steam Emulator and its forks
/// (gbe_fork / GSE): <c>&lt;saves root&gt;\&lt;appid&gt;\achievements.json</c>.
/// <code>
/// { "ACH_WIN_ONE_GAME": { "earned": true, "earned_time": 1617203350 }, ... }
/// </code>
/// Forks may add extra fields (progress, max_progress); unknown fields are ignored.
/// The file may list only earned achievements, so a missing key never means "known locked".
/// </summary>
public static class GoldbergStateParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    /// <exception cref="FormatException">The text is empty, truncated or not a JSON object.</exception>
    public static AchievementStateSnapshot Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            // Goldberg truncates then rewrites the file; an empty read is almost always a write in progress.
            throw new FormatException("State file is empty (possibly mid-write).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException("State file is not valid JSON (possibly incomplete write).", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"State file root must be an object, found {document.RootElement.ValueKind}.");
            }

            var entries = new Dictionary<string, AchievementStateEntry>(StringComparer.Ordinal);
            var warnings = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    warnings.Add("Skipped entry with empty achievement name.");
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"Skipped '{property.Name}': expected object, found {property.Value.ValueKind}.");
                    continue;
                }

                var earned = ReadBool(property.Value, "earned", warnings, property.Name);
                var earnedTime = ReadUnixTime(property.Value, "earned_time", warnings, property.Name);
                entries[property.Name] = new AchievementStateEntry(property.Name, earned, earned ? earnedTime : null);
            }

            return new AchievementStateSnapshot(entries, warnings);
        }
    }

    private static bool ReadBool(JsonElement obj, string name, List<string> warnings, string api)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return false;
            case JsonValueKind.Number when value.TryGetInt64(out var n):
                return n != 0;
            case JsonValueKind.String:
                var s = value.GetString();
                if (bool.TryParse(s, out var b))
                {
                    return b;
                }

                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ns))
                {
                    return ns != 0;
                }

                break;
        }

        warnings.Add($"'{api}': unrecognized '{name}' value; treated as not earned.");
        return false;
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement obj, string name, List<string> warnings, string api)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        long seconds;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var n):
                seconds = n;
                break;
            case JsonValueKind.Number when value.TryGetDouble(out var d):
                seconds = (long)d;
                break;
            case JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ns):
                seconds = ns;
                break;
            case JsonValueKind.Null:
                return null;
            default:
                warnings.Add($"'{api}': unrecognized '{name}' value ignored.");
                return null;
        }

        if (seconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            warnings.Add($"'{api}': '{name}' out of range ignored.");
            return null;
        }
    }
}

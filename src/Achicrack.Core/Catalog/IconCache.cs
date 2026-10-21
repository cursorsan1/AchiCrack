using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Catalog;

/// <summary>
/// Copies achievement icons from game folders into Achicrack's own cache so they stay available
/// (and readable without touching the game folder) after registration. Missing icons yield null,
/// which the UI renders as the default trophy.
/// </summary>
public sealed class IconCache(string cacheDirectory, ILogger? logger = null)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico",
    };

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string CacheDirectory { get; } = cacheDirectory;

    /// <summary>Returns the cached copy of a local icon, or null if the source is missing/unsupported.</summary>
    public string? GetOrAdd(long gameId, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        if (Uri.TryCreate(sourcePath, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return null; // remote icons are only used through the optional online provider
        }

        try
        {
            var source = new FileInfo(sourcePath);
            if (!source.Exists || !AllowedExtensions.Contains(source.Extension) || source.Length > 8 * 1024 * 1024)
            {
                return null;
            }

            var dir = Path.Combine(CacheDirectory, gameId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, HashName(source.FullName) + source.Extension.ToLowerInvariant());
            var targetInfo = new FileInfo(target);
            if (!targetInfo.Exists || targetInfo.Length != source.Length || targetInfo.LastWriteTimeUtc < source.LastWriteTimeUtc)
            {
                var temp = target + ".tmp";
                File.Copy(source.FullName, temp, overwrite: true);
                File.Move(temp, target, overwrite: true);
            }

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not cache icon {Path}", sourcePath);
            return null;
        }
    }

    public void Purge(long gameId)
    {
        try
        {
            var dir = Path.Combine(CacheDirectory, gameId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not purge icon cache for game {GameId}", gameId);
        }
    }

    private static string HashName(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24].ToLowerInvariant();
}

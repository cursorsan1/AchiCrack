using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Achicrack.App.Infrastructure;

/// <summary>Loads icons from disk without locking the file; returns null on any failure.</summary>
internal static class ImageLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    public static ImageSource? Load(string? path, bool grayscale = false, int decodeWidth = 128)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var key = $"{path}|{grayscale}|{decodeWidth}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // read fully, release the file
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                if (grayscale)
                {
                    var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
                    gray.Freeze();
                    image = gray;
                }
                else
                {
                    image = bitmap;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or InvalidOperationException
                                       or UnauthorizedAccessException or ArgumentException)
        {
            image = null;
        }

        lock (Gate)
        {
            Cache[key] = image;
        }

        return image;
    }

    public static void ClearCache()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }
}

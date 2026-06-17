using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mystral.Services;

public static class IconImageSource
{
    private static readonly Dictionary<string, ImageSource> SiteImageCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource LoadSiteImage(string relativePath)
    {
        var normalizedPath = relativePath.TrimStart('/', '\\').Replace('\\', '/');
        if (SiteImageCache.TryGetValue(normalizedPath, out var cached))
        {
            return cached;
        }

        var source = normalizedPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
            ? LoadBestFrame(normalizedPath)
            : LoadBitmap(normalizedPath);
        SiteImageCache[normalizedPath] = source;
        return source;
    }

    public static ImageSource LoadBestFrame(string relativePath)
    {
        return LoadFrame(relativePath, targetSize: null);
    }

    public static ImageSource LoadBestFitFrame(string relativePath, int targetSize)
    {
        return LoadFrame(relativePath, targetSize);
    }

    private static ImageSource LoadFrame(string relativePath, int? targetSize)
    {
        var normalizedPath = relativePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var iconPath = Path.Combine(AppContext.BaseDirectory, normalizedPath);

        var decoder = BitmapDecoder.Create(
            new Uri(iconPath, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frames = decoder.Frames;
        var frame = targetSize is null
            ? frames
                .OrderByDescending(static candidate => candidate.PixelWidth * candidate.PixelHeight)
                .ThenByDescending(static candidate => candidate.Format.BitsPerPixel)
                .First()
            : frames
                .OrderBy(candidate => Math.Abs(candidate.PixelWidth - targetSize.Value) + Math.Abs(candidate.PixelHeight - targetSize.Value))
                .ThenBy(candidate => candidate.PixelWidth < targetSize.Value || candidate.PixelHeight < targetSize.Value)
                .ThenByDescending(static candidate => candidate.Format.BitsPerPixel)
                .ThenBy(static candidate => candidate.PixelWidth * candidate.PixelHeight)
                .First();

        frame.Freeze();
        return frame;
    }

    private static ImageSource LoadBitmap(string normalizedPath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri($"pack://siteoforigin:,,,/{normalizedPath}", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

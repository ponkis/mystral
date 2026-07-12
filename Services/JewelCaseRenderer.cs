using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mystral.Services;

internal sealed class JewelCaseRenderer
{
    public const int OutputWidth = 352;
    public const int OutputHeight = 317;
    private const int BaseX = 26;
    private const int BaseY = 6;
    private const int BaseWidth = 301;
    private const int BaseHeight = 306;
    // The case's left spine makes its usable tray sit slightly right of the
    // full canvas center. Keep both smart layers aligned to that tray.
    private const int ArtworkX = BaseX + 10;
    private const int ArtworkY = 8;
    private const int ArtworkSize = 301;
    private static readonly Lazy<Task<FixedLayers>> SharedLayers = new(
        () => Task.Run(LoadFixedLayers),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly FixedLayers _layers;
    private readonly object _artworkLock = new();
    private PixelBuffer? _coverArtwork;
    private PixelBuffer? _discArtwork;

    private JewelCaseRenderer(FixedLayers layers)
    {
        _layers = layers;
    }

    public bool HasDiscArtwork
    {
        get
        {
            lock (_artworkLock)
            {
                return _discArtwork is not null;
            }
        }
    }

    public static async Task<JewelCaseRenderer> CreateAsync(CancellationToken cancellationToken = default)
    {
        var layers = await SharedLayers.Value.WaitAsync(cancellationToken);
        return new JewelCaseRenderer(layers);
    }

    public async Task SetArtworkAsync(
        BitmapSource? coverArtwork,
        BitmapSource? discArtwork,
        CancellationToken cancellationToken = default)
    {
        EnsureThreadSafe(coverArtwork);
        EnsureThreadSafe(discArtwork);
        var prepared = await Task.Run(() =>
        {
            PixelBuffer? cover = coverArtwork is null
                ? null
                : ResizeAspectFill(ReadPixels(coverArtwork), ArtworkSize, ArtworkSize, cancellationToken);
            PixelBuffer? disc = discArtwork is null
                ? null
                : ResizeAspectFill(ReadPixels(discArtwork), ArtworkSize, ArtworkSize, cancellationToken);
            return (Cover: cover, Disc: disc);
        }, cancellationToken);

        lock (_artworkLock)
        {
            _coverArtwork = prepared.Cover;
            _discArtwork = prepared.Disc;
        }
    }

    public Task<BitmapSource> RenderAsync(
        double coverOpacity,
        double discAngle,
        CancellationToken cancellationToken = default)
    {
        PixelBuffer? cover;
        PixelBuffer? disc;
        lock (_artworkLock)
        {
            cover = _coverArtwork;
            disc = _discArtwork;
        }

        return Task.Run(
            () => Render(cover, disc, coverOpacity, discAngle, cancellationToken),
            cancellationToken);
    }

    private BitmapSource Render(
        PixelBuffer? cover,
        PixelBuffer? disc,
        double coverOpacity,
        double discAngle,
        CancellationToken cancellationToken)
    {
        var pixels = new byte[OutputWidth * OutputHeight * 4];
        BlitNormal(pixels, OutputWidth, OutputHeight, _layers.Base, BaseX, BaseY, 1);
        if (disc is not null)
        {
            BlitRotatedDisc(pixels, disc.Value, discAngle, cancellationToken);
        }

        if (cover is not null && coverOpacity > 0.001)
        {
            BlitNormal(
                pixels,
                OutputWidth,
                OutputHeight,
                cover.Value,
                ArtworkX,
                ArtworkY,
                Math.Clamp(coverOpacity, 0, 1));
        }

        ApplyCaseEffects(pixels, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(
            OutputWidth,
            OutputHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            OutputWidth * 4);
        result.Freeze();
        return result;
    }

    private void ApplyCaseEffects(byte[] destination, CancellationToken cancellationToken)
    {
        var shadow = _layers.Shadow.Pixels;
        var highlight = _layers.Highlight.Pixels;
        for (var y = 0; y < OutputHeight; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < OutputWidth; x++)
            {
                var offset = ((y * OutputWidth) + x) * 4;
                var isOverTray = x >= BaseX
                    && x < BaseX + BaseWidth
                    && y >= BaseY
                    && y < BaseY + BaseHeight;
                if (isOverTray)
                {
                    destination[offset] = (byte)(((destination[offset] * shadow[offset]) + 127) / 255);
                    destination[offset + 1] = (byte)(((destination[offset + 1] * shadow[offset + 1]) + 127) / 255);
                    destination[offset + 2] = (byte)(((destination[offset + 2] * shadow[offset + 2]) + 127) / 255);
                    destination[offset] = Math.Max(destination[offset], highlight[offset]);
                    destination[offset + 1] = Math.Max(destination[offset + 1], highlight[offset + 1]);
                    destination[offset + 2] = Math.Max(destination[offset + 2], highlight[offset + 2]);
                    destination[offset + 3] = byte.MaxValue;
                    continue;
                }

                var shadowStrength = byte.MaxValue - Luminance(shadow, offset);
                var highlightStrength = Luminance(highlight, offset);
                var strength = Math.Max(shadowStrength, highlightStrength);
                if (strength < 5)
                {
                    continue;
                }

                if (highlightStrength >= shadowStrength)
                {
                    destination[offset] = highlight[offset];
                    destination[offset + 1] = highlight[offset + 1];
                    destination[offset + 2] = highlight[offset + 2];
                }

                destination[offset + 3] = (byte)strength;
            }
        }
    }

    private static void BlitRotatedDisc(
        byte[] destination,
        PixelBuffer disc,
        double angle,
        CancellationToken cancellationToken)
    {
        var radians = -angle * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var center = (ArtworkSize - 1) / 2.0;
        var outerRadius = ArtworkSize * 0.493;
        var innerRadius = ArtworkSize * 0.074;

        for (var y = 0; y < ArtworkSize; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var deltaY = y - center;
            for (var x = 0; x < ArtworkSize; x++)
            {
                var deltaX = x - center;
                var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                if (distance >= outerRadius || distance <= innerRadius)
                {
                    continue;
                }

                var sourceX = (cosine * deltaX) - (sine * deltaY) + center;
                var sourceY = (sine * deltaX) + (cosine * deltaY) + center;
                var sampled = SampleBilinear(disc, sourceX, sourceY);
                var edgeAlpha = Math.Min(
                    Math.Clamp(outerRadius - distance, 0, 1),
                    Math.Clamp(distance - innerRadius, 0, 1));
                var alpha = (byte)Math.Clamp(Math.Round(sampled.Alpha * edgeAlpha), 0, 255);
                BlendNormalPixel(
                    destination,
                    (((ArtworkY + y) * OutputWidth) + ArtworkX + x) * 4,
                    sampled.Blue,
                    sampled.Green,
                    sampled.Red,
                    alpha);
            }
        }
    }

    private static void BlitNormal(
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        PixelBuffer source,
        int left,
        int top,
        double opacity)
    {
        for (var y = 0; y < source.Height; y++)
        {
            var destinationY = top + y;
            if (destinationY < 0 || destinationY >= destinationHeight)
            {
                continue;
            }

            for (var x = 0; x < source.Width; x++)
            {
                var destinationX = left + x;
                if (destinationX < 0 || destinationX >= destinationWidth)
                {
                    continue;
                }

                var sourceOffset = ((y * source.Width) + x) * 4;
                var alpha = (byte)Math.Clamp(Math.Round(source.Pixels[sourceOffset + 3] * opacity), 0, 255);
                BlendNormalPixel(
                    destination,
                    ((destinationY * destinationWidth) + destinationX) * 4,
                    source.Pixels[sourceOffset],
                    source.Pixels[sourceOffset + 1],
                    source.Pixels[sourceOffset + 2],
                    alpha);
            }
        }
    }

    private static void BlendNormalPixel(
        byte[] destination,
        int offset,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        if (alpha == 0)
        {
            return;
        }

        var destinationAlpha = destination[offset + 3];
        if (alpha == byte.MaxValue || destinationAlpha == 0)
        {
            destination[offset] = blue;
            destination[offset + 1] = green;
            destination[offset + 2] = red;
            destination[offset + 3] = alpha;
            return;
        }

        var sourceAlpha = alpha / 255.0;
        var backdropAlpha = destinationAlpha / 255.0;
        var outputAlpha = sourceAlpha + (backdropAlpha * (1 - sourceAlpha));
        destination[offset] = BlendNormalChannel(destination[offset], blue, sourceAlpha, backdropAlpha, outputAlpha);
        destination[offset + 1] = BlendNormalChannel(destination[offset + 1], green, sourceAlpha, backdropAlpha, outputAlpha);
        destination[offset + 2] = BlendNormalChannel(destination[offset + 2], red, sourceAlpha, backdropAlpha, outputAlpha);
        destination[offset + 3] = (byte)Math.Clamp(Math.Round(outputAlpha * 255), 0, 255);
    }

    private static byte BlendNormalChannel(
        byte backdrop,
        byte source,
        double sourceAlpha,
        double backdropAlpha,
        double outputAlpha)
    {
        var premultiplied = (sourceAlpha * source) + (backdropAlpha * (1 - sourceAlpha) * backdrop);
        return (byte)Math.Clamp(Math.Round(premultiplied / outputAlpha), 0, 255);
    }

    private static Pixel SampleBilinear(PixelBuffer source, double x, double y)
    {
        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, source.Width - 1);
        var y1 = Math.Min(y0 + 1, source.Height - 1);
        var xWeight = x - x0;
        var yWeight = y - y0;
        var topLeft = ((y0 * source.Width) + x0) * 4;
        var topRight = ((y0 * source.Width) + x1) * 4;
        var bottomLeft = ((y1 * source.Width) + x0) * 4;
        var bottomRight = ((y1 * source.Width) + x1) * 4;
        return new Pixel(
            Interpolate(source.Pixels, topLeft, topRight, bottomLeft, bottomRight, 0, xWeight, yWeight),
            Interpolate(source.Pixels, topLeft, topRight, bottomLeft, bottomRight, 1, xWeight, yWeight),
            Interpolate(source.Pixels, topLeft, topRight, bottomLeft, bottomRight, 2, xWeight, yWeight),
            Interpolate(source.Pixels, topLeft, topRight, bottomLeft, bottomRight, 3, xWeight, yWeight));
    }

    private static byte Interpolate(
        byte[] pixels,
        int topLeft,
        int topRight,
        int bottomLeft,
        int bottomRight,
        int channel,
        double xWeight,
        double yWeight)
    {
        var top = pixels[topLeft + channel] + ((pixels[topRight + channel] - pixels[topLeft + channel]) * xWeight);
        var bottom = pixels[bottomLeft + channel] + ((pixels[bottomRight + channel] - pixels[bottomLeft + channel]) * xWeight);
        return (byte)Math.Clamp(Math.Round(top + ((bottom - top) * yWeight)), 0, 255);
    }

    private static FixedLayers LoadFixedLayers()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Resources", "Images");
        return new FixedLayers(
            ResizeStretch(LoadPixels(Path.Combine(directory, "jewel_base.png")), BaseWidth, BaseHeight, CancellationToken.None),
            ResizeStretch(LoadPixels(Path.Combine(directory, "jewel_shadow.png")), OutputWidth, OutputHeight, CancellationToken.None),
            ResizeStretch(LoadPixels(Path.Combine(directory, "jewel_highlight.png")), OutputWidth, OutputHeight, CancellationToken.None));
    }

    private static PixelBuffer LoadPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        return ReadPixels(decoder.Frames[0]);
    }

    private static PixelBuffer ReadPixels(BitmapSource source)
    {
        BitmapSource bgra = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return new PixelBuffer(bgra.PixelWidth, bgra.PixelHeight, pixels);
    }

    private static PixelBuffer ResizeAspectFill(
        PixelBuffer source,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var scale = Math.Max(width / (double)source.Width, height / (double)source.Height);
        var cropLeft = (source.Width - (width / scale)) / 2;
        var cropTop = (source.Height - (height / scale)) / 2;
        return Resize(source, width, height, scale, cropLeft, cropTop, cancellationToken);
    }

    private static PixelBuffer ResizeStretch(
        PixelBuffer source,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var destination = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var sourceY = ((y + 0.5) * source.Height / height) - 0.5;
            for (var x = 0; x < width; x++)
            {
                var sourceX = ((x + 0.5) * source.Width / width) - 0.5;
                WriteSample(destination, ((y * width) + x) * 4, SampleBilinear(source, sourceX, sourceY));
            }
        }

        return new PixelBuffer(width, height, destination);
    }

    private static PixelBuffer Resize(
        PixelBuffer source,
        int width,
        int height,
        double scale,
        double cropLeft,
        double cropTop,
        CancellationToken cancellationToken)
    {
        var destination = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var sourceY = cropTop + ((y + 0.5) / scale) - 0.5;
            for (var x = 0; x < width; x++)
            {
                var sourceX = cropLeft + ((x + 0.5) / scale) - 0.5;
                WriteSample(destination, ((y * width) + x) * 4, SampleBilinear(source, sourceX, sourceY));
            }
        }

        return new PixelBuffer(width, height, destination);
    }

    private static void WriteSample(byte[] destination, int offset, Pixel pixel)
    {
        destination[offset] = pixel.Blue;
        destination[offset + 1] = pixel.Green;
        destination[offset + 2] = pixel.Red;
        destination[offset + 3] = pixel.Alpha;
    }

    private static byte Luminance(byte[] pixels, int offset)
    {
        return (byte)(((pixels[offset + 2] * 54)
            + (pixels[offset + 1] * 183)
            + (pixels[offset] * 19)
            + 128) >> 8);
    }

    private static void EnsureThreadSafe(BitmapSource? source)
    {
        if (source is not null && !source.IsFrozen)
        {
            throw new InvalidOperationException("Artwork previews must be frozen before rendering.");
        }
    }

    private sealed record FixedLayers(PixelBuffer Base, PixelBuffer Shadow, PixelBuffer Highlight);
    private readonly record struct PixelBuffer(int Width, int Height, byte[] Pixels);
    private readonly record struct Pixel(byte Blue, byte Green, byte Red, byte Alpha);
}

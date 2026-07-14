using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mystral.Services;

internal static class CdArtworkComposer
{
    private const string CenterLayerFileName = "cd_center.png";
    private const string ScreenLayerFileName = "cd_black.png";
    private const string MultiplyLayerFileName = "cd_white.png";
    private const string MaskFileName = "cd_mask.png";
    private const string DefaultArtworkFileName = "cd_default_cover.png";

    private static string ImagesDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        "Images");

    public static Task<BitmapSource> ComposeDefaultAsync(CancellationToken cancellationToken = default)
    {
        return ComposeAsync(Path.Combine(ImagesDirectory, DefaultArtworkFileName), cancellationToken);
    }

    public static Task<BitmapSource> ComposeAsync(
        string artworkPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artworkPath);
        var fullArtworkPath = Path.GetFullPath(artworkPath);

        return Task.Run(
            () => ComposeWithDiscLayers(LoadPixels(fullArtworkPath), cancellationToken),
            cancellationToken);
    }

    public static Task<BitmapSource> ComposeAsync(
        BitmapSource artwork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        cancellationToken.ThrowIfCancellationRequested();
        if (!artwork.IsFrozen)
        {
            var artworkSnapshot = ReadPixels(artwork);
            return Task.Run(
                () => ComposeWithDiscLayers(artworkSnapshot, cancellationToken),
                cancellationToken);
        }

        return Task.Run(
            () => ComposeWithDiscLayers(ReadPixels(artwork), cancellationToken),
            cancellationToken);
    }

    internal static BitmapSource Compose(
        BitmapSource artwork,
        BitmapSource mask,
        BitmapSource multiplyLayer,
        BitmapSource screenLayer,
        BitmapSource centerLayer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(multiplyLayer);
        ArgumentNullException.ThrowIfNull(screenLayer);
        ArgumentNullException.ThrowIfNull(centerLayer);

        var artworkPixels = ReadPixels(artwork);
        var maskPixels = ReadPixels(mask);
        var targetWidth = maskPixels.Width;
        var targetHeight = maskPixels.Height;
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new InvalidDataException("The CD mask has no pixels.");
        }

        var outputPixels = CreateMaskedArtwork(
            artworkPixels,
            maskPixels,
            cancellationToken);
        BlendLayer(outputPixels, ReadPixels(multiplyLayer), targetWidth, targetHeight, MultiplyLayerFileName, BlendMode.Multiply, cancellationToken);
        BlendLayer(outputPixels, ReadPixels(screenLayer), targetWidth, targetHeight, ScreenLayerFileName, BlendMode.Screen, cancellationToken);
        BlendLayer(outputPixels, ReadPixels(centerLayer), targetWidth, targetHeight, CenterLayerFileName, BlendMode.Normal, cancellationToken);
        return CreateResult(outputPixels, targetWidth, targetHeight, cancellationToken);
    }

    private static BitmapSource ComposeWithDiscLayers(
        PixelBuffer artwork,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mask = LoadDiscLayer(MaskFileName);
        if (mask.Width <= 0 || mask.Height <= 0)
        {
            throw new InvalidDataException("The CD mask has no pixels.");
        }

        var targetWidth = mask.Width;
        var targetHeight = mask.Height;
        var outputPixels = CreateMaskedArtwork(artwork, mask, cancellationToken);
        artwork = default;
        mask = default;
        BlendDiscLayer(outputPixels, MultiplyLayerFileName, targetWidth, targetHeight, BlendMode.Multiply, cancellationToken);
        BlendDiscLayer(outputPixels, ScreenLayerFileName, targetWidth, targetHeight, BlendMode.Screen, cancellationToken);
        BlendDiscLayer(outputPixels, CenterLayerFileName, targetWidth, targetHeight, BlendMode.Normal, cancellationToken);
        return CreateResult(outputPixels, targetWidth, targetHeight, cancellationToken);
    }

    private static PixelBuffer LoadDiscLayer(string fileName)
    {
        return LoadPixels(Path.Combine(ImagesDirectory, fileName));
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
        BitmapSource bgraSource = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            bgraSource = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var stride = checked(bgraSource.PixelWidth * 4);
        var pixels = new byte[checked(stride * bgraSource.PixelHeight)];
        bgraSource.CopyPixels(pixels, stride, 0);
        return new PixelBuffer(bgraSource.PixelWidth, bgraSource.PixelHeight, pixels);
    }

    private static byte[] CreateMaskedArtwork(
        PixelBuffer artwork,
        PixelBuffer mask,
        CancellationToken cancellationToken)
    {
        var resizedArtwork = ResizeAspectFill(
            artwork,
            mask.Width,
            mask.Height,
            cancellationToken);
        return ApplyArtworkMask(resizedArtwork, mask.Pixels, cancellationToken);
    }

    private static void BlendDiscLayer(
        byte[] output,
        string fileName,
        int width,
        int height,
        BlendMode mode,
        CancellationToken cancellationToken)
    {
        BlendLayer(
            output,
            LoadDiscLayer(fileName),
            width,
            height,
            fileName,
            mode,
            cancellationToken);
    }

    private static void BlendLayer(
        byte[] output,
        PixelBuffer layer,
        int width,
        int height,
        string fileName,
        BlendMode mode,
        CancellationToken cancellationToken)
    {
        ValidateLayerSize(layer, width, height, fileName);
        Blend(output, layer.Pixels, mode, cancellationToken);
    }

    private static BitmapSource CreateResult(
        byte[] pixels,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            checked(width * 4));
        result.Freeze();
        return result;
    }

    private static byte[] ResizeAspectFill(
        PixelBuffer sourcePixels,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        if (sourcePixels.Width <= 0 || sourcePixels.Height <= 0)
        {
            throw new InvalidDataException("The CD artwork has no pixels.");
        }

        var destination = new byte[checked(targetWidth * targetHeight * 4)];
        var scale = Math.Max(
            targetWidth / (double)sourcePixels.Width,
            targetHeight / (double)sourcePixels.Height);
        var cropLeft = (sourcePixels.Width - (targetWidth / scale)) / 2;
        var cropTop = (sourcePixels.Height - (targetHeight / scale)) / 2;

        for (var y = 0; y < targetHeight; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var sourceY = Math.Clamp(
                cropTop + ((y + 0.5) / scale) - 0.5,
                0,
                sourcePixels.Height - 1);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(y0 + 1, sourcePixels.Height - 1);
            var yWeight = sourceY - y0;

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Clamp(
                    cropLeft + ((x + 0.5) / scale) - 0.5,
                    0,
                    sourcePixels.Width - 1);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(x0 + 1, sourcePixels.Width - 1);
                var xWeight = sourceX - x0;

                var topLeft = ((y0 * sourcePixels.Width) + x0) * 4;
                var topRight = ((y0 * sourcePixels.Width) + x1) * 4;
                var bottomLeft = ((y1 * sourcePixels.Width) + x0) * 4;
                var bottomRight = ((y1 * sourcePixels.Width) + x1) * 4;
                var destinationOffset = ((y * targetWidth) + x) * 4;

                SampleBilinearPremultiplied(
                    sourcePixels.Pixels,
                    topLeft,
                    topRight,
                    bottomLeft,
                    bottomRight,
                    xWeight,
                    yWeight,
                    destination,
                    destinationOffset);
            }
        }

        return destination;
    }

    private static void SampleBilinearPremultiplied(
        byte[] source,
        int topLeft,
        int topRight,
        int bottomLeft,
        int bottomRight,
        double xWeight,
        double yWeight,
        byte[] destination,
        int destinationOffset)
    {
        var topLeftWeight = (1 - xWeight) * (1 - yWeight);
        var topRightWeight = xWeight * (1 - yWeight);
        var bottomLeftWeight = (1 - xWeight) * yWeight;
        var bottomRightWeight = xWeight * yWeight;
        var alpha =
            (source[topLeft + 3] * topLeftWeight)
            + (source[topRight + 3] * topRightWeight)
            + (source[bottomLeft + 3] * bottomLeftWeight)
            + (source[bottomRight + 3] * bottomRightWeight);

        destination[destinationOffset + 3] = ToByte(alpha);
        if (alpha <= double.Epsilon)
        {
            destination[destinationOffset] = 0;
            destination[destinationOffset + 1] = 0;
            destination[destinationOffset + 2] = 0;
            return;
        }

        for (var channel = 0; channel < 3; channel++)
        {
            var premultiplied =
                (source[topLeft + channel] * source[topLeft + 3] * topLeftWeight)
                + (source[topRight + channel] * source[topRight + 3] * topRightWeight)
                + (source[bottomLeft + channel] * source[bottomLeft + 3] * bottomLeftWeight)
                + (source[bottomRight + channel] * source[bottomRight + 3] * bottomRightWeight);
            destination[destinationOffset + channel] = ToByte(premultiplied / alpha);
        }
    }

    private static byte[] ApplyArtworkMask(
        byte[] artwork,
        byte[] mask,
        CancellationToken cancellationToken)
    {
        if (artwork.Length != mask.Length)
        {
            throw new InvalidDataException("The CD mask dimensions do not match the artwork canvas.");
        }

        var output = new byte[artwork.Length];
        for (var offset = 0; offset < artwork.Length; offset += 4)
        {
            if ((offset & 0x7ffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            output[offset] = artwork[offset];
            output[offset + 1] = artwork[offset + 1];
            output[offset + 2] = artwork[offset + 2];

            var luminance = (
                (mask[offset + 2] * 54)
                + (mask[offset + 1] * 183)
                + (mask[offset] * 19)
                + 128) >> 8;
            var maskAlpha = MultiplyByte((byte)luminance, mask[offset + 3]);
            output[offset + 3] = MultiplyByte(artwork[offset + 3], maskAlpha);
        }

        return output;
    }

    private static void Blend(
        byte[] backdrop,
        byte[] source,
        BlendMode mode,
        CancellationToken cancellationToken)
    {
        if (backdrop.Length != source.Length)
        {
            throw new InvalidDataException("A CD effect layer has the wrong dimensions.");
        }

        for (var offset = 0; offset < backdrop.Length; offset += 4)
        {
            if ((offset & 0x7ffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var sourceAlphaByte = source[offset + 3];
            if (sourceAlphaByte == 0)
            {
                continue;
            }

            var backdropAlphaByte = backdrop[offset + 3];
            if (backdropAlphaByte == 0)
            {
                Buffer.BlockCopy(source, offset, backdrop, offset, 4);
                continue;
            }

            if (sourceAlphaByte == byte.MaxValue && backdropAlphaByte == byte.MaxValue)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    backdrop[offset + channel] = mode switch
                    {
                        BlendMode.Multiply => MultiplyByte(backdrop[offset + channel], source[offset + channel]),
                        BlendMode.Screen => (byte)(byte.MaxValue - MultiplyByte(
                            (byte)(byte.MaxValue - backdrop[offset + channel]),
                            (byte)(byte.MaxValue - source[offset + channel]))),
                        _ => source[offset + channel]
                    };
                }

                continue;
            }

            var sourceAlpha = sourceAlphaByte / 255.0;
            var backdropAlpha = backdropAlphaByte / 255.0;
            var outputAlpha = sourceAlpha + (backdropAlpha * (1 - sourceAlpha));

            for (var channel = 0; channel < 3; channel++)
            {
                var backdropColor = backdrop[offset + channel] / 255.0;
                var sourceColor = source[offset + channel] / 255.0;
                var blendedColor = mode switch
                {
                    BlendMode.Multiply => backdropColor * sourceColor,
                    BlendMode.Screen => 1 - ((1 - backdropColor) * (1 - sourceColor)),
                    _ => sourceColor
                };
                var premultipliedColor =
                    (sourceAlpha * (1 - backdropAlpha) * sourceColor)
                    + (sourceAlpha * backdropAlpha * blendedColor)
                    + (backdropAlpha * (1 - sourceAlpha) * backdropColor);
                backdrop[offset + channel] = ToByte((premultipliedColor / outputAlpha) * 255);
            }

            backdrop[offset + 3] = ToByte(outputAlpha * 255);
        }
    }

    private static void ValidateLayerSize(PixelBuffer layer, int width, int height, string fileName)
    {
        if (layer.Width != width || layer.Height != height)
        {
            throw new InvalidDataException(
                $"{fileName} must be {width}x{height}, but it is {layer.Width}x{layer.Height}.");
        }
    }

    private static byte MultiplyByte(byte left, byte right)
    {
        return (byte)(((left * right) + 127) / 255);
    }

    private static byte ToByte(double value)
    {
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private readonly record struct PixelBuffer(int Width, int Height, byte[] Pixels);

    private enum BlendMode
    {
        Normal,
        Multiply,
        Screen
    }
}

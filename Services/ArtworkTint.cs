using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mystral.Services;

public static class ArtworkTint
{
    public static Color? ExtractDominantTint(BitmapSource? source)
    {
        if (source is null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return null;
        }

        try
        {
            BitmapSource sampled = source;
            var maxSide = Math.Max(source.PixelWidth, source.PixelHeight);
            if (maxSide > 64)
            {
                var scale = 64.0 / maxSide;
                sampled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            if (sampled.Format != PixelFormats.Bgra32)
            {
                sampled = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);
            }

            var stride = sampled.PixelWidth * 4;
            var pixels = new byte[stride * sampled.PixelHeight];
            sampled.CopyPixels(pixels, stride, 0);

            double total = 0;
            double red = 0;
            double green = 0;
            double blue = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var a = pixels[i + 3];

                if (a < 128)
                {
                    continue;
                }

                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var brightness = (r + g + b) / 3.0;

                if (brightness < 24 || brightness > 238)
                {
                    continue;
                }

                var saturation = max == 0 ? 0 : (max - min) / (double)max;
                var weight = (a / 255.0) * (0.30 + saturation * 1.85);

                if (brightness < 70)
                {
                    weight *= 0.55;
                }
                else if (brightness > 210)
                {
                    weight *= 0.65;
                }

                total += weight;
                red += r * weight;
                green += g * weight;
                blue += b * weight;
            }

            if (total <= 0.01)
            {
                return null;
            }

            return PolishTint(Color.FromRgb(
                ClampByte(red / total),
                ClampByte(green / total),
                ClampByte(blue / total)));
        }
        catch
        {
            return null;
        }
    }

    public static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            ClampByte(from.R + (to.R - from.R) * amount),
            ClampByte(from.G + (to.G - from.G) * amount),
            ClampByte(from.B + (to.B - from.B) * amount));
    }

    public static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color PolishTint(Color color)
    {
        color = Saturate(color, 1.22);

        var luminance = color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;
        if (luminance < 66)
        {
            return Blend(color, Colors.White, Math.Min(0.34, (66 - luminance) / 150.0));
        }

        return luminance > 178
            ? Blend(color, Colors.Black, Math.Min(0.30, (luminance - 178) / 140.0))
            : color;
    }

    private static Color Saturate(Color color, double factor)
    {
        var gray = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
        return Color.FromRgb(
            ClampByte(gray + (color.R - gray) * factor),
            ClampByte(gray + (color.G - gray) * factor),
            ClampByte(gray + (color.B - gray) * factor));
    }

    private static byte ClampByte(double value)
    {
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}

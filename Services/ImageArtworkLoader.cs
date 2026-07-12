using System.IO;
using System.Windows.Media.Imaging;
using Mystral.Models;

namespace Mystral.Services;

public sealed class ImageArtworkLoader
{
    public async Task<ArtworkAsset> LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = await File.ReadAllBytesAsync(path, cancellationToken);
        return await LoadAsync(data, cancellationToken);
    }

    public Task<ArtworkAsset> LoadAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            throw new InvalidDataException("The selected image is empty.");
        }

        return Task.Run(() => Load(data, cancellationToken), cancellationToken);
    }

    public BitmapSource LoadApplicationImage(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Images", fileName);
        return Load(File.ReadAllBytes(path), CancellationToken.None).Preview;
    }

    internal static ArtworkAsset Load(byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0
                || decoder.Frames[0].PixelWidth <= 0
                || decoder.Frames[0].PixelHeight <= 0)
            {
                throw new InvalidDataException("The selected file does not contain a valid image.");
            }

            BitmapSource preview = decoder.Frames[0];
            if (!preview.IsFrozen)
            {
                preview.Freeze();
            }

            return new ArtworkAsset(data, preview);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is NotSupportedException
                                   or FileFormatException
                                   or ArgumentException
                                   or IOException)
        {
            throw new InvalidDataException("The selected file is not a supported image.", ex);
        }
    }
}

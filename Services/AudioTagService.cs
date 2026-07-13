using System.Globalization;
using System.IO;
using ATL.AudioData;
using Mystral.Models;

namespace Mystral.Services;

public sealed class AudioTagService
{
    private const int CopyBufferSize = 1024 * 1024;
    private const int MaxSuggestedFileNameLength = 255;

    static AudioTagService()
    {
        ATL.Settings.UseFileNameWhenNoTitle = false;
        ATL.Settings.NullAbsentValues = true;
    }

    public Task<BurnTrackDraft> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return Task.Run(() => Read(fullPath, cancellationToken), cancellationToken);
    }

    public static string CreateSuggestedOutputFileName(BurnTrackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var extension = Path.GetExtension(draft.SourcePath);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var title = new string(draft.Title
            .Trim()
            .Select(character => character < ' ' || invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (title.Length == 0)
        {
            title = "Untitled";
        }

        if (IsReservedWindowsFileName(title))
        {
            title = $"_{title}";
        }

        var maximumTitleLength = Math.Max(1, MaxSuggestedFileNameLength - extension.Length);
        if (title.Length > maximumTitleLength)
        {
            title = title[..maximumTitleLength].TrimEnd('.', ' ');
        }

        return $"{title}{extension}";
    }

    public async Task SaveCopyAsync(
        BurnTrackDraft draft,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var sourcePath = Path.GetFullPath(draft.SourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(sourcePath, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a different file so the original audio is preserved.");
        }

        var sourceExtension = Path.GetExtension(sourcePath);
        if (!string.Equals(sourceExtension, Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The saved copy must keep the original {sourceExtension} format.");
        }

        ValidateDraft(draft);

        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The destination folder is invalid.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.mystral{sourceExtension}");

        try
        {
            await CopyWithProgressAsync(sourcePath, temporaryPath, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var track = new ATL.Track(temporaryPath);
                ApplyDraft(track, draft);
                var tagProgress = progress is null
                    ? null
                    : new Progress<float>(value => progress.Report(0.76 + (Math.Clamp(value, 0, 1) * 0.24)));
                if (!await track.SaveAsync(tagProgress))
                {
                    throw new IOException("The selected audio format could not save the requested metadata.");
                }
            }
            catch (Exception ex) when (IsAudioParsingFailure(ex))
            {
                throw new InvalidDataException("The audio copy could not accept the requested metadata.", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private BurnTrackDraft Read(string path, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The selected audio file no longer exists.", path);
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan))
            {
                var reader = AudioDataIOFactory.GetInstance().GetFromStream(stream);
                if (reader.AudioFormat.DataFormat.ID == ATL.Format.UNKNOWN_FORMAT.ID)
                {
                    throw new InvalidDataException("The selected file does not have a recognized audio signature.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var track = new ATL.Track(path);
            if (track.TechnicalInformation.AudioDataSize <= 0 || track.DurationMs <= 0)
            {
                throw new InvalidDataException("The selected file does not contain valid audio data.");
            }

            var cover = LoadEmbeddedArtwork(
                track,
                ATL.PictureInfo.PIC_TYPE.Front,
                fallbackToAnyPicture: true,
                cancellationToken,
                out var coverPictureType);
            var disc = LoadEmbeddedArtwork(
                track,
                ATL.PictureInfo.PIC_TYPE.CD,
                fallbackToAnyPicture: false,
                cancellationToken,
                out _);
            return new BurnTrackDraft
            {
                SourcePath = path,
                Title = track.Title?.Trim() ?? string.Empty,
                Artist = track.Artist?.Trim() ?? string.Empty,
                Genre = track.Genre?.Trim() ?? string.Empty,
                Year = FormatYear(track),
                Album = track.Album?.Trim() ?? string.Empty,
                TrackNumber = track.TrackNumberStr?.Trim()
                    ?? track.TrackNumber?.ToString(CultureInfo.InvariantCulture)
                    ?? string.Empty,
                TrackTotal = track.TrackTotal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Isrc = track.ISRC?.Trim() ?? string.Empty,
                Duration = TimeSpan.FromMilliseconds(track.DurationMs),
                CoverArtwork = cover,
                DiscArtwork = disc,
                CoverArtworkOriginalType = coverPictureType,
                CoverArtworkOriginalData = cover?.Data
            };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (IsAudioParsingFailure(ex))
        {
            throw new InvalidDataException("The selected file has a recognized audio header, but its audio structure is invalid.", ex);
        }
    }

    private static bool IsAudioParsingFailure(Exception exception)
    {
        return exception is ArgumentException
            or ArithmeticException
            or EndOfStreamException
            or FormatException
            or IndexOutOfRangeException
            or InvalidOperationException
            or KeyNotFoundException
            or NotSupportedException
            or NullReferenceException;
    }

    private static ArtworkAsset? LoadEmbeddedArtwork(
        ATL.Track track,
        ATL.PictureInfo.PIC_TYPE type,
        bool fallbackToAnyPicture,
        CancellationToken cancellationToken,
        out ATL.PictureInfo.PIC_TYPE? loadedPictureType)
    {
        loadedPictureType = null;
        var picture = track.EmbeddedPictures.FirstOrDefault(item => item.PicType == type);
        if (picture is null && fallbackToAnyPicture)
        {
            picture = track.EmbeddedPictures.FirstOrDefault(item => item.PicType != ATL.PictureInfo.PIC_TYPE.CD);
        }

        if (picture?.PictureData is not { Length: > 0 } data)
        {
            return null;
        }

        try
        {
            var artwork = ImageArtworkLoader.Load(data.ToArray(), cancellationToken);
            loadedPictureType = picture.PicType;
            return artwork;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task CopyWithProgressAsync(
        string sourcePath,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (source.Length > 0)
            {
                progress?.Report((copied / (double)source.Length) * 0.75);
            }
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static void ApplyDraft(ATL.Track track, BurnTrackDraft draft)
    {
        track.Title = draft.Title.Trim();
        track.Artist = draft.Artist.Trim();
        track.Genre = draft.Genre.Trim();
        track.Album = draft.Album.Trim();
        track.TrackNumberStr = draft.TrackNumber.Trim();
        track.TrackTotal = ParseTrackTotal(draft.TrackTotal, draft.TrackNumber);
        ApplyYear(track, draft.Year);

        if (draft.CoverArtworkChanged)
        {
            ReplacePicture(
                track,
                draft.CoverArtwork?.Data,
                ATL.PictureInfo.PIC_TYPE.Front,
                draft.CoverArtworkOriginalType,
                draft.CoverArtworkOriginalData);
        }

        if (draft.DiscArtworkChanged)
        {
            ReplacePicture(track, draft.DiscArtwork?.Data, ATL.PictureInfo.PIC_TYPE.CD);
        }
    }

    private static void ApplyYear(ATL.Track track, string value)
    {
        var year = ParseYear(value);
        track.Date = null;
        track.Year = year;
    }

    internal static void ValidateDraft(BurnTrackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateFieldLength("Title", draft.Title, BurnTrackDraft.MaxTitleLength);
        ValidateFieldLength("Artist", draft.Artist, BurnTrackDraft.MaxArtistLength);
        ValidateFieldLength("Album", draft.Album, BurnTrackDraft.MaxAlbumLength);
        ValidateFieldLength("Genre", draft.Genre, BurnTrackDraft.MaxGenreLength);
        ValidateFieldLength("Year", draft.Year, BurnTrackDraft.MaxYearLength);
        ValidateFieldLength("Track number", draft.TrackNumber, BurnTrackDraft.MaxTrackNumberLength);
        ValidateFieldLength("Track count", draft.TrackTotal, BurnTrackDraft.MaxTrackTotalLength);
        _ = ParseYear(draft.Year);
        var trackNumber = ParseTrackNumber(draft.TrackNumber);
        _ = ParseTrackTotal(draft.TrackTotal, trackNumber);
    }

    private static void ValidateFieldLength(string fieldName, string value, int maximumLength)
    {
        if (value.Length > maximumLength)
        {
            throw new InvalidDataException($"{fieldName} cannot be longer than {maximumLength} characters.");
        }
    }

    private static int? ParseYear(string value)
    {
        var yearText = value.Trim();
        if (yearText.Length == 0)
        {
            return null;
        }

        if (yearText.Length == 4
            && int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1 and <= 9999)
        {
            return year;
        }

        throw new InvalidDataException("Year must contain exactly four digits.");
    }

    private static int? ParseTrackNumber(string value)
    {
        return ParseTrackValue(value, "Track number");
    }

    private static int? ParseTrackTotal(string value, string trackNumber)
    {
        return ParseTrackTotal(value, ParseTrackNumber(trackNumber));
    }

    private static int? ParseTrackTotal(string value, int? trackNumber)
    {
        var total = ParseTrackValue(value, "Track count");

        if (trackNumber is { } number && total is { } count && number > count)
        {
            throw new InvalidDataException("Track number cannot be greater than the track count.");
        }

        return total;
    }

    private static int? ParseTrackValue(string value, string fieldName)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > BurnTrackDraft.MaxTrackValue)
        {
            throw new InvalidDataException(
                $"{fieldName} must be a whole number from 1 to {BurnTrackDraft.MaxTrackValue}.");
        }

        return parsed;
    }

    private static bool IsReservedWindowsFileName(string fileName)
    {
        var name = fileName.Split('.')[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && name[3] is >= '1' and <= '9';
    }

    private static string FormatYear(ATL.Track track)
    {
        var year = track.Year ?? track.Date?.Year;
        return year?.ToString("0000", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void ReplacePicture(
        ATL.Track track,
        byte[]? data,
        ATL.PictureInfo.PIC_TYPE type,
        ATL.PictureInfo.PIC_TYPE? originalFallbackType = null,
        byte[]? originalFallbackData = null)
    {
        if (data is null
            && originalFallbackType is { } fallbackType
            && fallbackType != type
            && originalFallbackData is { Length: > 0 })
        {
            var fallback = track.EmbeddedPictures.FirstOrDefault(item =>
                item.PicType == fallbackType
                && item.PictureData is { Length: > 0 } pictureData
                && pictureData.AsSpan().SequenceEqual(originalFallbackData));
            if (fallback is not null)
            {
                track.EmbeddedPictures.Remove(fallback);
            }
        }

        foreach (var existing in track.EmbeddedPictures.Where(item => item.PicType == type).ToArray())
        {
            track.EmbeddedPictures.Remove(existing);
        }

        if (data is { Length: > 0 })
        {
            track.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(data, type));
        }
    }
}

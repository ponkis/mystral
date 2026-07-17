using Mystral.Models;

namespace Mystral.Services;

internal static class AppleMusicMediaMetadata
{
    private static readonly string[] CombinedArtistAlbumSeparators = [" \u2014 ", " \u2013 "];

    public static bool IsAppleMusicSource(string? sourceApp)
    {
        return !string.IsNullOrWhiteSpace(sourceApp)
            && (sourceApp.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
                || sourceApp.Contains("Apple Music", StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveArtist(string? artist, string? albumArtist, string? sourceApp)
    {
        var primaryArtist = artist?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(primaryArtist) || !IsAppleMusicSource(sourceApp))
        {
            return primaryArtist;
        }

        return albumArtist?.Trim() ?? string.Empty;
    }

    public static MediaSnapshot NormalizeLyricsLookup(MediaSnapshot snapshot)
    {
        if (!snapshot.HasSession
            || !IsAppleMusicSource(snapshot.SourceApp)
            || !string.IsNullOrWhiteSpace(snapshot.Album)
            || string.IsNullOrWhiteSpace(snapshot.Artist))
        {
            return snapshot;
        }

        foreach (var separator in CombinedArtistAlbumSeparators)
        {
            var separatorIndex = snapshot.Artist.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0
                || separatorIndex >= snapshot.Artist.Length - separator.Length)
            {
                continue;
            }

            var artist = snapshot.Artist[..separatorIndex].Trim();
            var album = snapshot.Artist[(separatorIndex + separator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album))
            {
                return snapshot with
                {
                    Artist = artist,
                    Album = album
                };
            }
        }

        return snapshot;
    }
}

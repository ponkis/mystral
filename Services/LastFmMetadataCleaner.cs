using System.Text.RegularExpressions;
using Mystral.Models;

namespace Mystral.Services;

public static partial class LastFmMetadataCleaner
{
    private static readonly string[] ArtistAlbumSeparators = [" - ", " -- ", " \u2014 ", " \u2013 "];
    private static readonly string[] NonSongTitles =
    [
        "advertisement",
        "advertisements",
        "ad",
        "ads",
        "podcast",
        "episode",
        "livestream",
        "live stream",
        "news",
        "audiobook",
        "unknown track",
        "no active track"
    ];

    public static LastFmTrackQuery CreateQuery(MediaSnapshot snapshot)
    {
        var title = CleanTrackName(snapshot.Title);
        var album = CleanAlbumName(snapshot.Album);
        var artist = CleanArtistName(snapshot.Artist, album);

        if (string.IsNullOrWhiteSpace(artist))
        {
            SplitArtistAndTitle(title, out artist, out title);
        }

        return new LastFmTrackQuery(title, artist, album);
    }

    public static bool IsLikelySong(MediaSnapshot snapshot, LastFmTrackQuery query, out string reason)
    {
        if (!snapshot.HasSession)
        {
            reason = "idle";
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.TrackName) || string.IsNullOrWhiteSpace(query.ArtistName))
        {
            reason = "missing artist or title";
            return false;
        }

        if (snapshot.Duration > TimeSpan.Zero && snapshot.Duration < TimeSpan.FromSeconds(30))
        {
            reason = "too short";
            return false;
        }

        var loweredTitle = query.TrackName.Trim().ToLowerInvariant();
        if (NonSongTitles.Any(nonSong => loweredTitle.Equals(nonSong, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "not a song";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static string CleanArtistName(string value, string albumName = "")
    {
        var artist = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(albumName))
        {
            foreach (var separator in ArtistAlbumSeparators)
            {
                var suffix = separator + albumName;
                if (artist.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    artist = artist[..^suffix.Length].Trim();
                    break;
                }
            }
        }

        foreach (var separator in ArtistAlbumSeparators)
        {
            var separatorIndex = artist.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                artist = artist[..separatorIndex].Trim();
                break;
            }
        }

        return artist;
    }

    public static string CleanTrackName(string value)
    {
        var title = NormalizeWhitespace(value);
        title = VideoNoisePattern().Replace(title, string.Empty);
        title = TrailingSitePattern().Replace(title, string.Empty);
        return NormalizeWhitespace(title);
    }

    public static string CleanAlbumName(string value)
    {
        var album = NormalizeWhitespace(value);
        album = VideoNoisePattern().Replace(album, string.Empty);
        return NormalizeWhitespace(album);
    }

    private static void SplitArtistAndTitle(string value, out string artist, out string title)
    {
        artist = string.Empty;
        title = value;

        foreach (var separator in ArtistAlbumSeparators)
        {
            var separatorIndex = value.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex >= value.Length - separator.Length)
            {
                continue;
            }

            artist = CleanArtistName(value[..separatorIndex]);
            title = CleanTrackName(value[(separatorIndex + separator.Length)..]);
            return;
        }
    }

    private static string NormalizeWhitespace(string value)
    {
        return WhitespacePattern().Replace(value ?? string.Empty, " ").Trim();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\s*[\[\(]\s*(official\s+)?((music|lyric)\s+)?(video|audio|lyrics?|visualizer|hd|4k)\s*[\]\)]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VideoNoisePattern();

    [GeneratedRegex(@"\s+-\s+(youtube|spotify|apple music|soundcloud)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSitePattern();
}

namespace Mystral.Models;

public sealed record LyricsTrackInfo(
    string TrackName,
    string ArtistName,
    string AlbumName,
    double Duration,
    string SourceName);

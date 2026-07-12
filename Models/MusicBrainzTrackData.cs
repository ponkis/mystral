namespace Mystral.Models;

public sealed record MusicBrainzTrackData(
    string Title,
    string Artist,
    string Genre,
    string Date,
    string Album,
    string TrackNumber,
    byte[]? CoverArtwork,
    byte[]? DiscArtwork);

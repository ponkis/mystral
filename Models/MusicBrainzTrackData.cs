namespace Mystral.Models;

public sealed record MusicBrainzTrackData(
    string Title,
    string Artist,
    string Genre,
    string Year,
    string Album,
    string TrackNumber,
    string TrackTotal,
    byte[]? CoverArtwork,
    byte[]? DiscArtwork);

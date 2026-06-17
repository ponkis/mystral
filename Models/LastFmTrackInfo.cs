namespace Mystral.Models;

public sealed record LastFmTrackInfo(
    string TrackName,
    string ArtistName,
    string Url,
    string AlbumName,
    TimeSpan Duration);

public sealed record LastFmTrackQuery(
    string TrackName,
    string ArtistName,
    string AlbumName);

public sealed record LastFmSubmitResult(bool IsSuccess, string Message)
{
    public static LastFmSubmitResult Success(string message) => new(true, message);
    public static LastFmSubmitResult Failure(string message) => new(false, message);
}

public sealed record LastFmValidationResult(bool IsSuccess, string Message)
{
    public static LastFmValidationResult Success { get; } = new(true, "Last.fm API key and username verified.");
}

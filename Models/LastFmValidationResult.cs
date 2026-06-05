namespace Mystral.Models;

public sealed record LastFmValidationResult(bool IsSuccess, string Message)
{
    public static LastFmValidationResult Success { get; } = new(true, "Last.fm API key and username verified.");
}

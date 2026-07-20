using System.Windows.Media.Imaging;

namespace Mystral.Models;

public sealed record MediaSnapshot(
    bool HasSession,
    string Title,
    string Artist,
    string Album,
    string SourceApp,
    string Description,
    string StatusText,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    bool CanPlay,
    bool CanPause,
    bool CanNext,
    bool CanPrevious,
    bool CanSeek,
    BitmapImage? CoverArt)
{
    public DateTimeOffset TimelineUpdatedAt { get; init; }

    public bool HasReliableTimelineUpdatedAt { get; init; }

    public string CoverArtFingerprint { get; init; } = string.Empty;

    public static MediaSnapshot Empty { get; } = new(
        HasSession: false,
        Title: "No active track",
        Artist: string.Empty,
        Album: string.Empty,
        SourceApp: string.Empty,
        Description: "Start playback in any media app",
        StatusText: "Idle",
        Position: TimeSpan.Zero,
        Duration: TimeSpan.Zero,
        IsPlaying: false,
        CanPlay: false,
        CanPause: false,
        CanNext: false,
        CanPrevious: false,
        CanSeek: false,
        CoverArt: null);
}

public sealed record WindowPlacement(double Left, double Top);

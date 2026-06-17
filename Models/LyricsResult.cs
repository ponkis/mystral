namespace Mystral.Models;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed record LyricsTrackInfo(
    string TrackName,
    string ArtistName,
    string AlbumName,
    double Duration,
    string SourceName);

public sealed record LyricsResult(
    LyricsStatus Status,
    IReadOnlyList<LyricLine> SyncedLines,
    IReadOnlyList<string> PlainLines,
    string Message,
    LyricsTrackInfo? TrackInfo = null)
{
    public static LyricsResult Empty { get; } = new(LyricsStatus.Empty, [], [], "Lyrics will appear when a track is playing");
    public static LyricsResult Loading { get; } = new(LyricsStatus.Loading, [], [], "Looking for lyrics");
    public static LyricsResult Instrumental { get; } = new(LyricsStatus.Instrumental, [], [], "Instrumental track");
    public static LyricsResult NotFound { get; } = new(LyricsStatus.NotFound, [], [], "No lyrics found");

    public static LyricsResult Synced(IReadOnlyList<LyricLine> lines, LyricsTrackInfo? trackInfo = null)
    {
        return new LyricsResult(LyricsStatus.Synced, lines, [], "Synced lyrics", trackInfo);
    }

    public static LyricsResult Plain(IReadOnlyList<string> lines, LyricsTrackInfo? trackInfo = null)
    {
        return new LyricsResult(LyricsStatus.Plain, [], lines, "Unsynced lyrics", trackInfo);
    }

    public static LyricsResult InstrumentalWithInfo(LyricsTrackInfo? trackInfo)
    {
        return new LyricsResult(LyricsStatus.Instrumental, [], [], "Instrumental track", trackInfo);
    }

    public static LyricsResult Error(string message)
    {
        return new LyricsResult(LyricsStatus.Error, [], [], message);
    }
}
